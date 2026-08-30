using System.IO;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ProjectMage;
using ProjectMage.gamestate;

namespace SaS2Resalter;

public static class RuntimeReloadPatch
{
    private static FileSystemWatcher _lootWatcher;
    private static FileSystemWatcher _monsterWatcher;
    private static FileSystemWatcher _dialogWatcher;

    public static void Init()
    {
        var configDir = Path.GetDirectoryName(Plugin.Instance.Config.ConfigFilePath);
        if (string.IsNullOrEmpty(configDir))
            return;

        Plugin.Instance.Log.LogInfo($"Loading and Watching {Path.Combine(configDir, "amione.SaS2Resalter")}");

        var dataDir = Path.Combine(configDir, "amione.SaS2Resalter");
        Plugin.CustomLootPath = Path.Combine(dataDir, "loot.zls");
        Plugin.CustomMonstersPath = Path.Combine(dataDir, "monsters.zms");
        Plugin.CustomDialogPath = Path.Combine(dataDir, "Dialog", "data", "dialog.zdx");

        // Ensure the directory exists so the watcher can be created
        Directory.CreateDirectory(dataDir);

        // Watch the directory for loot.zls changes
        _lootWatcher = new FileSystemWatcher(dataDir, "loot.zls")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _lootWatcher.Changed += (_, _) => Plugin.PendingLootReload = true;
        _lootWatcher.Created += (_, _) => Plugin.PendingLootReload = true;

        // Watch the directory for monsters.zms changes
        _monsterWatcher = new FileSystemWatcher(dataDir, "monsters.zms")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _monsterWatcher.Changed += (_, _) => Plugin.PendingMonsterReload = true;
        _monsterWatcher.Created += (_, _) => Plugin.PendingMonsterReload = true;

        // Watch the dialog override directory for dialog.zdx changes
        var dialogDir = Path.Combine(dataDir, "Dialog", "data");
        Directory.CreateDirectory(dialogDir);
        _dialogWatcher = new FileSystemWatcher(dialogDir, "dialog.zdx")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _dialogWatcher.Changed += (_, _) => Plugin.PendingDialogReload = true;
        _dialogWatcher.Created += (_, _) => Plugin.PendingDialogReload = true;

        Plugin.Instance.Log.LogInfo($"Watching {dataDir} for catalog changes.");
    }

    [HarmonyPatch(typeof(Game1), "Update", typeof(GameTime))]
    [HarmonyPostfix]
    private static void Game1UpdatePostfix()
    {
        if (!Plugin.PendingLootReload && !Plugin.PendingMonsterReload && !Plugin.PendingDialogReload) return;

        // Only reload when no mission is active
        var session = GameSessionMgr.gameSession;
        if (session is { activeMission: >= 0 }) return;

        // Loot reload
        if (Plugin.PendingLootReload)
        {
            Plugin.PendingLootReload = false;
            if (File.Exists(Plugin.CustomLootPath))
            {
                try
                {
                    LoadPatch.ApplyCustomLoot();
                    Plugin.Instance.Log.LogInfo("Loot catalog hot-reloaded.");
                }
                catch (System.Exception ex)
                {
                    Plugin.Instance.Log.LogError($"Hot-reload loot failed: {ex}");
                }
            }
        }

        // Monster reload
        if (Plugin.PendingMonsterReload)
        {
            Plugin.PendingMonsterReload = false;
            if (!File.Exists(Plugin.CustomMonstersPath)) return;
            try
            {
                MonsterLoadPatch.ApplyCustomMonsters(Plugin.CustomMonstersPath);
                Plugin.Instance.Log.LogInfo("Monster catalog hot-reloaded.");
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Log.LogError($"Hot-reload monsters failed: {ex}");
            }
        }

        // Dialog reload: re-read the dialog catalog (merchant shop scripts).
        if (Plugin.PendingDialogReload)
        {
            Plugin.PendingDialogReload = false;
            if (!File.Exists(Plugin.CustomDialogPath)) return;
            try
            {
                DialogOverridePatch.ReloadDialog();
                Plugin.Instance.Log.LogInfo("Dialog catalog hot-reloaded.");
            }
            catch (System.Exception ex)
            {
                Plugin.Instance.Log.LogError($"Hot-reload dialog failed: {ex}");
            }
        }
    }

    /// <summary>Allow other code to schedule a reload on the next safe frame.</summary>
    public static void TriggerLootReload() => Plugin.PendingLootReload = true;

    public static void TriggerMonsterReload() => Plugin.PendingMonsterReload = true;

    public static void TriggerDialogReload() => Plugin.PendingDialogReload = true;
}