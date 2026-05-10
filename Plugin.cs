using System.IO;
using BepInEx;
using BepInEx.NetLauncher.Common;
using HarmonyLib;

namespace SaS2Resalter;

[BepInPlugin(PluginInfo.PluginGuid, PluginInfo.PluginName, PluginInfo.PluginVersion)]
public class Plugin : BasePlugin
{
    internal static Plugin Instance;
    internal static string CustomLootPath;

    public override void Load()
    {
        Instance = this;

        var configDir = Path.GetDirectoryName(Config.ConfigFilePath); // .../BepInEx/config
        if (configDir != null) CustomLootPath = Path.Combine(configDir, "amione.SaS2Resalter", "loot.zls");

        if (!File.Exists(CustomLootPath))
            Log.LogWarning($"No custom loot file at {CustomLootPath}. Vanilla catalog will be used.");
        else
            Log.LogInfo($"Custom loot.zls found at {CustomLootPath}");

        // Set up the runtime‑reload watcher
        RuntimeReloadPatch.Init(CustomLootPath);

        var harmony = new Harmony(PluginInfo.PluginGuid);
        harmony.PatchAll();
        Instance.Log.LogInfo($"{PluginInfo.PluginName} v{PluginInfo.PluginVersion} loaded.");
    }
}