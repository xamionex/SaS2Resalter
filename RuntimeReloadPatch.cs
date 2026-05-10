using System.IO;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ProjectMage;
using ProjectMage.gamestate;

namespace SaS2Resalter;

/// Watches loot.zls for changes and hot-reloads the catalog between missions.
/// Actual file I/O is delegated to LoadPatch.ApplyCustomLoot() so we never touch Loader.GetReader.
[HarmonyPatch]
public static class RuntimeReloadPatch
{
    private static string            _customPath;
    private static bool              _pendingReload;
    private static FileSystemWatcher _lootWatcher;

    public static void Init(string customPath)
    {
        _customPath = customPath;
        if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
            return;

        var dir  = Path.GetDirectoryName(customPath)!;
        var file = Path.GetFileName(customPath);

        _lootWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _lootWatcher.Changed += (_, _) => _pendingReload = true;
    }

    [HarmonyPatch(typeof(Game1), "Update", typeof(GameTime))]
    [HarmonyPostfix]
    private static void Game1UpdatePostfix()
    {
        if (!_pendingReload) return;
        if (string.IsNullOrEmpty(_customPath) || !File.Exists(_customPath)) return;

        // Only reload when no mission is active (safe moment to swap the catalog)
        var session = GameSessionMgr.gameSession;
        if (session is { activeMission: >= 0 }) return;

        _pendingReload = false;

        try
        {
            LoadPatch.ApplyCustomLoot();
            Plugin.Instance.Log.LogInfo("Loot catalog hot-reloaded from custom loot.zls.");
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogError($"Hot-reload failed: {ex}");
        }
    }

    /// Allow other code to schedule a reload on the next safe frame.
    public static void TriggerReload() => _pendingReload = true;
}