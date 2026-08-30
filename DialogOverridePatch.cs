using System.IO;
using BepInEx;
using Chronicler.dialog;
using HarmonyLib;
using ProjectMage.loader;

namespace SaS2Resalter;

/// <summary>
/// Redirects Loader.GetReader for the dialog catalog (Dialog/data/dialog.zdx) to a mirrored copy under BepInEx/config/amione.SaS2Resalter/ when one exists, falling back to the game's own file.
///
/// The dialog file holds every NPC's dialog tree, including merchant store scripts (the shop inventories).
/// The editor writes the merged dialog.zdx to config/amione.SaS2Resalter/Dialog/data/dialog.zdx and it takes precedence here.
/// </summary>
[HarmonyPatch]
public static class DialogOverridePatch
{
    private static string OverrideRoot => Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter");

    [HarmonyPatch(typeof(Loader), nameof(Loader.GetReader), typeof(string))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool GetReader_Prefix(ref string assetName, ref BinaryReader __result)
    {
        try
        {
            if (string.IsNullOrEmpty(assetName)) return true;

            var rel = assetName.Replace('\\', '/').TrimStart('/');
            if (!rel.Equals("Dialog/data/dialog.zdx", System.StringComparison.OrdinalIgnoreCase))
                return true;

            var candidate = Path.Combine(OverrideRoot, rel);
            if (!File.Exists(candidate)) return true;

            __result = new BinaryReader(File.Open(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            Plugin.Instance.Log.LogInfo("[DialogOverride] dialog.zdx -> custom override");
            return false; // skip original
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[DialogOverride] Failed to redirect dialog.zdx: {ex.Message}");
            return true; // fall back to vanilla on error
        }
    }

    /// <summary>Re-read the dialog catalog from the override file (hot reload).</summary>
    public static void ReloadDialog()
    {
        var path = Plugin.CustomDialogPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs);
        DialogMgr.Read(reader);
    }
}
