using System.IO;
using HarmonyLib;
using Bestiary.monsters;
using ProjectMage.gamestate;

namespace SaS2Resalter;

[HarmonyPatch]
public static class MonsterLoadPatch
{
    [HarmonyPatch(typeof(MonsterCatalog), nameof(MonsterCatalog.Read), typeof(string))]
    [HarmonyPrefix]
    private static bool ReadPrefix()
    {
        var customPath = Plugin.CustomMonstersPath;
        if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
            return true;

        try
        {
            ApplyCustomMonsters(customPath);
            Plugin.Instance.Log.LogInfo("Monster catalog loaded from custom monsters.zms.");
            return false; // skip original
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogError($"Failed to load custom monsters.zms: {ex}");
            return true; // fall back to vanilla
        }
    }

    /// Load the monster catalog from a custom file and run post-read setup.
    /// Can be called at startup and during hot-reloads.
    public static void ApplyCustomMonsters(string customPath)
    {
        using var fs = new FileStream(customPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs);
        MonsterCatalog.Read(reader); // reads the whole catalog

        // The original MonsterCatalog.Read(string) calls this after loading:
        GameSessionMgr.gameSession?.hazeburntMgr?.PopulateHazeburntMonsters();
    }
}