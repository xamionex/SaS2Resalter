using System.IO;
using BepInEx;
using Common.io;
using HarmonyLib;

namespace SaS2Resalter;

/// <summary>
/// Redirects raw game asset reads (master.zcm texture metadata, .zsx/.zmx character defs, etc.)
/// to a mirrored copy under BepInEx/config/amione.SaS2Resalter/ when one exists, falling back to
/// the game's own file otherwise.
///
/// The game opens these assets through Common.io.FileMgr.Open(string), where the path is a
/// game-relative path such as "Content/gfx/master.zcm" or "data/&lt;name&gt;.zmx". We mirror that
/// same relative layout under the config folder, so the editor's apply step can drop overrides at
/// e.g. config/amione.SaS2Resalter/Content/gfx/master.zcm and they take precedence.
///
/// Texture pixels (gfx/&lt;name&gt;.xnb) are handled separately by TextureOverridePatch (PNG).
/// </summary>
[HarmonyPatch]
public static class FileOverridePatch
{
    private static string OverrideRoot => Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter");

    [HarmonyPatch(typeof(FileMgr), nameof(FileMgr.Open), typeof(string))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static void Open_Prefix(ref string __0)
    {
        try
        {
            var path = __0;
            // Only mirror game-relative paths; leave already-absolute paths untouched.
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return;

            var rel = path.Replace('\\', '/').TrimStart('/');
            var candidate = Path.Combine(OverrideRoot, rel);
            if (!File.Exists(candidate)) return;

            __0 = candidate;
            Plugin.Instance.Log.LogInfo($"[FileOverride] {path} -> custom override");
        }
        catch
        {
            // On any error keep the original path so vanilla loading proceeds.
        }
    }
}
