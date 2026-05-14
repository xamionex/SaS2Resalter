using BepInEx;
using BepInEx.NET.Common;
using HarmonyLib;

namespace SaS2Resalter;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static string CustomLootPath;
    internal static string CustomMonstersPath;
    internal static bool PendingLootReload;
    internal static bool PendingMonsterReload;

    public override void Load()
    {
        Instance = this;

        // Set up the runtime-reload watcher
        RuntimeReloadPatch.Init();

        var harmony = new Harmony(PluginInfo.PluginGuid);
        harmony.PatchAll();
        Instance.Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded.");
    }
}