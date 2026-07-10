using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using Menumancer.UIFormat;
using CommonRect = Common.Rectangle;
using CommonTexture2D = Common.Texture2D;
using SpriteBatchImpl = CommonMonoGame.SpriteBatchImpl;

namespace SaS2Resalter;

/// <summary>
/// Draws custom item icons from a SEPARATE atlas so they don't need to fit in the vanilla
/// `items.xnb` (which is full and can't grow past the runtime's max texture size).
///
/// The game draws item icons through <c>Common.SpriteBatch.Draw(UIRender.itemsTex, ...,
/// Rectangle(img%32*128, img/32*128, 128, 128), ...)</c> (its own Common wrapper types, not the
/// MonoGame SpriteBatch). An item whose <c>img</c> is at/after the vanilla atlas capacity has a
/// source rect below the texture (Y &gt;= itemsTex.Height); we detect that and redirect the draw to
/// <c>textures/custom_items.png</c> at local index <c>img - capacity</c>.
///
/// The custom atlas PNG is produced by the editor's Apply step and loaded here as a
/// Common.Texture2D (same wrapper used for texture overrides).
/// </summary>
[HarmonyPatch]
public static class CustomIconPatch
{
    private const int TILE = 128;
    private const int COLS = 32;

    private static CommonTexture2D _atlas;
    private static long _lastMtime = -1;
    private static bool _diagItems;
    private static bool _diagCustom;

    private static string AtlasPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "textures", "custom_items.png");

    private static CommonTexture2D GetAtlas()
    {
        try
        {
            if (!File.Exists(AtlasPath))
            {
                _atlas = null;
                _lastMtime = -1;
                return null;
            }

            var mtime = new FileInfo(AtlasPath).LastWriteTime.Ticks;
            if (_atlas != null && mtime == _lastMtime) return _atlas;

            var loaded = TextureOverridePatch.LoadCommonTextureFromPng(AtlasPath);
            if (loaded == null) return _atlas;

            _atlas = loaded;
            _lastMtime = mtime;
            Plugin.Instance.Log.LogInfo($"[CustomIcon] Loaded custom atlas {_atlas.Width}x{_atlas.Height}.");
            return _atlas;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[CustomIcon] Failed to load custom atlas: {ex.Message}");
            return _atlas;
        }
    }

    private static void Redirect(ref CommonTexture2D texture, ref CommonRect? sourceRectangle)
    {
        try
        {
            var items = UIRender.itemsTex;
            // Fast path: only item-icon draws reference itemsTex.
            if (texture == null || items == null || !ReferenceEquals(texture, items)) return;
            if (!sourceRectangle.HasValue) return;

            if (!_diagItems)
            {
                _diagItems = true;
                Plugin.Instance.Log.LogInfo(
                    $"[CustomIcon] item-icon draws active; itemsTex {texture.Width}x{texture.Height}, capacity={texture.Height / TILE * COLS}.");
            }

            var r = sourceRectangle.Value;
            if (r.Y < texture.Height) return; // a normal (vanilla) icon

            if (!_diagCustom)
            {
                _diagCustom = true;
                Plugin.Instance.Log.LogInfo(
                    $"[CustomIcon] custom-icon draw detected (rect Y={r.Y}, height={texture.Height}).");
            }

            var atlas = GetAtlas();
            if (atlas == null) return;

            var cap = texture.Height / TILE * COLS;
            var img = r.Y / TILE * COLS + r.X / TILE;
            var local = img - cap;
            if (local < 0) return;

            var cx = local % COLS * TILE;
            var cy = local / COLS * TILE;
            if (cx + TILE > atlas.Width || cy + TILE > atlas.Height) return;

            texture = atlas;
            sourceRectangle = new CommonRect(cx, cy, TILE, TILE);
        }
        catch
        {
            // On any error leave the draw untouched.
        }
    }

    /// Common.SpriteBatch is an interface; the concrete SpriteBatchImpl implements Draw explicitly
    /// (method name "Common.SpriteBatch.Draw"). Patch the two 9-argument, position-based overloads
    /// (float scale and Vector2 scale) that item icons use.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var mth in typeof(SpriteBatchImpl).GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (mth.Name != "Common.SpriteBatch.Draw") continue;
            var ps = mth.GetParameters();
            if (ps.Length == 9
                && ps[1].ParameterType == typeof(Common.Vector2)
                && ps[2].ParameterType == typeof(CommonRect?))
            {
                yield return mth;
            }
        }
    }

    // texture = arg 0, sourceRectangle = arg 2 (same in both 9-arg overloads).
    [HarmonyPrefix]
    // ReSharper disable InconsistentNaming
    private static void Prefix(ref CommonTexture2D __0, ref CommonRect? __2) =>
        Redirect(ref __0, ref __2);
}
