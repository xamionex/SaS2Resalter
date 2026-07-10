using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using ProjectMage;
using ProjectMage.loader;
using CommonTexture2D = Common.Texture2D;
using XnaTexture2D = Microsoft.Xna.Framework.Graphics.Texture2D;
using XnaColor = Microsoft.Xna.Framework.Color;

namespace SaS2Resalter;

/// <summary>
/// Overrides "gfx/&lt;name&gt;" texture loads with a custom PNG from
/// BepInEx/config/amione.SaS2Resalter/textures/&lt;name&gt;.png, falling back to the game's
/// content when no override exists.
///
/// The game loads textures as the abstract Common.Texture2D via Loader.LoadTask&lt;T&gt;.Process
/// (-&gt; Common.ContentManager.Load). That abstraction exposes no "create texture from pixels"
/// API, so we decode the PNG with our own managed PngDecoder, create a MonoGame Texture2D and
/// upload pixels via SetData (using the real device behind Game1, which extends MonoGame's Game),
/// premultiply it (XNB content is premultiplied), then wrap the MonoGame texture into a
/// Common.Texture2D. Texture2D.FromStream is intentionally avoided because it pulls in
/// SharpDX.MediaFoundation/Mathematics, which are unavailable in the game's runtime.
///
/// Everything is fail-safe: any problem logs and lets the original (vanilla) load run.
/// </summary>
[HarmonyPatch]
public static class TextureOverridePatch
{
    private static string TexturesDir => Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "textures");

    // Cached reflection handles for the Common.Texture2D wrapper, resolved on first use.
    private static bool _wrapResolved;
    private static ConstructorInfo _wrapCtor;
    private static FieldInfo _wrapXnaField; // optional: a field on the wrapper holding the Xna texture

    /// Patch the closed generic LoadTask&lt;Common.Texture2D&gt;.Process so only texture loads are touched.
    private static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Loader.LoadTask<CommonTexture2D>), "Process");

    private static bool Prefix(object __instance, ref bool __result)
    {
        try
        {
            var assetName = AccessTools.Field(__instance.GetType(), "assetName")?.GetValue(__instance) as string;
            var pngPath = ResolveOverridePath(assetName);
            if (pngPath == null) return true; // no override -> vanilla load

            var common = LoadCommonTextureFromPng(pngPath);
            if (common == null) return true; // failed -> vanilla load

            // Complete the task through its own (locked) CompleteTask so GetIsComplete stays correct.
            var complete = AccessTools.Method(__instance.GetType(), "CompleteTask");
            __result = (bool)complete.Invoke(__instance, [common]);
            Plugin.Instance.Log.LogInfo($"[TextureOverride] Loaded custom texture for '{assetName}'.");
            return false; // skip original
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[TextureOverride] Override failed, using vanilla: {ex.Message}");
            return true;
        }
    }

    /// Returns the PNG path overriding the given asset, or null. Asset names look like "gfx/sprites".
    private static string ResolveOverridePath(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return null;
        // Take the part after the last '/' (e.g. "gfx/sub/foo" -> "foo").
        var slash = assetName.LastIndexOf('/');
        var name = slash >= 0 ? assetName.Substring(slash + 1) : assetName;
        if (string.IsNullOrEmpty(name)) return null;

        var path = Path.Combine(TexturesDir, name + ".png");
        return File.Exists(path) ? path : null;
    }

    internal static CommonTexture2D LoadCommonTextureFromPng(string pngPath)
    {
        var gd = Game1.Instance?.GraphicsDevice;
        if (gd == null)
        {
            Plugin.Instance.Log.LogWarning("[TextureOverride] No GraphicsDevice yet.");
            return null;
        }

        // Decode the PNG ourselves. Texture2D.FromStream uses an image-loading path that depends on
        // SharpDX.MediaFoundation/Mathematics, which are unavailable in the game's runtime, so we
        // build the texture from decoded pixels via SetData (a core path that works).
        var img = PngDecoder.Decode(File.ReadAllBytes(pngPath));
        var xna = new XnaTexture2D(gd, img.Width, img.Height);

        // PNG is straight (non-premultiplied) alpha; the game's content is premultiplied.
        var data = new XnaColor[img.Width * img.Height];
        for (var i = 0; i < data.Length; i++)
        {
            var o = i * 4;
            data[i] = XnaColor.FromNonPremultiplied(
                img.Rgba[o], img.Rgba[o + 1], img.Rgba[o + 2], img.Rgba[o + 3]);
        }

        xna.SetData(data);

        return WrapXnaTexture(xna);
    }

    /// Wrap a MonoGame Texture2D into the game's Common.Texture2D abstraction.
    private static CommonTexture2D WrapXnaTexture(XnaTexture2D xna)
    {
        // Fast path: Common.Texture2D may already be (assignable from) the MonoGame type.
        if (typeof(CommonTexture2D).IsInstanceOfType(xna))
            return (CommonTexture2D)(object)xna;

        if (!_wrapResolved) ResolveWrap();

        if (_wrapCtor != null)
        {
            var args = _wrapCtor.GetParameters()
                .Select(p => p.ParameterType.IsInstanceOfType(xna) ? (object)xna : Default(p.ParameterType))
                .ToArray();
            var instance = (CommonTexture2D)_wrapCtor.Invoke(args);
            if (_wrapXnaField != null) _wrapXnaField.SetValue(instance, xna);
            return instance;
        }

        Plugin.Instance.Log.LogError("[TextureOverride] Could not resolve a Common.Texture2D wrapper for the MonoGame texture.");
        return null;
    }

    /// Find a concrete Common.Texture2D type with a constructor that accepts the MonoGame Texture2D.
    private static void ResolveWrap()
    {
        _wrapResolved = true;
        var baseType = typeof(CommonTexture2D);

        // Candidate concrete types: Common.Texture2D itself (if constructible) and its subclasses
        // (e.g. Texture2DImpl) across the loaded assemblies.
        //
        // Skip SharpDX assemblies entirely: enumerating their types (e.g. the MediaFoundation ones)
        // force-loads fields typed from SharpDX.Mathematics, which is missing in the game's runtime
        // and throws a TypeLoadException. The wrapper only ever lives in the game's Common assembly,
        // so SharpDX is never a candidate anyway. The per-type predicate is also exception-guarded.
        var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !(a.GetName().Name ?? string.Empty).StartsWith("SharpDX"))
            .SelectMany(SafeGetTypes)
            .Where(t => IsWrapCandidate(t, baseType))
            .ToList();

        foreach (var t in candidates)
        {
            var ctor = t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Any(p => p.ParameterType.IsAssignableFrom(typeof(XnaTexture2D))));
            if (ctor == null) continue;

            _wrapCtor = ctor;
            _wrapXnaField = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(f => f.FieldType.IsAssignableFrom(typeof(XnaTexture2D)));
            Plugin.Instance.Log.LogInfo(
                $"[TextureOverride] Wrapper resolved: {t.FullName}({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name))}).");
            return;
        }

        Plugin.Instance.Log.LogError(
            $"[TextureOverride] No constructible Common.Texture2D wrapper found (candidates: {string.Join(", ", candidates.Select(c => c.FullName))}).");
    }

    private static Type[] SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).ToArray(); }
        catch { return Type.EmptyTypes; }
    }

    /// Exception-guarded check: some types (e.g. ones with fields from missing assemblies) throw
    /// TypeLoadException when their hierarchy is inspected, which must not abort the whole scan.
    private static bool IsWrapCandidate(Type t, Type baseType)
    {
        try { return t != null && !t.IsAbstract && baseType.IsAssignableFrom(t); }
        catch { return false; }
    }

    private static object Default(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;
}
