using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Bestiary.monsters;
using HarmonyLib;
using ProjectMage.gamestate.hazeburnt;

namespace SaS2Resalter;

/// <summary>
/// Replaces the vanilla per-area hazeburnt spawn pools with the configured monster lists.
///
/// Config: BepInEx/config/amione.SaS2Resalter/hazeburnt_spawns.json
/// <code>
/// {
///   "1": ["burnt_skelbling", "hazewraith"],
///   "2": ["hazelady"]
/// }
/// </code>
/// Keys are area numbers (1..8, 0 is unused), values are monster def names.
/// Areas that are not listed stay empty, so an applied preset fully decides which enemies the hazeburnt system can spawn.
/// Written by the editor on apply.
///
/// Monsters that are not flagged hazeburnt in vanilla get the flag applied here, so a custom pick behaves like a hazeburnt monster (haze pile on death, hazeburnt hostility rules and so on).
/// </summary>
[HarmonyPatch]
public static class HazeburntSpawnPatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "hazeburnt_spawns.json");

    private static Dictionary<int, List<string>> _config;
    private static long _lastFileTime;

    /// Maximum area index the vanilla manager tracks (areaHazeburnts starts with 9 empty lists).
    private const int MaxArea = 8;

    public static void ReloadConfig() => _config = null;

    private static Dictionary<int, List<string>> Config
    {
        get
        {
            var empty = new Dictionary<int, List<string>>();
            if (!File.Exists(ConfigPath)) return _config ??= empty;
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_config != null && _lastFileTime == mtime) return _config;
            try
            {
                _config = SimpleJson.ParseIntKeyedStringLists(File.ReadAllText(ConfigPath));
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[HazeburntSpawn] Loaded spawn lists for {_config.Count} area(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[HazeburntSpawn] Config error: {ex.Message}");
                _config = empty;
            }

            return _config;
        }
    }

    /// Rebuild the area pools from the config instead of the vanilla monsterField[63] mapping.
    /// Runs on every catalog (re)load, including the loader's own monsters.zms read.
    [HarmonyPatch(typeof(HazeburntMgr), nameof(HazeburntMgr.PopulateHazeburntMonsters))]
    [HarmonyPrefix]
    private static bool Populate_Prefix(HazeburntMgr __instance)
    {
        var config = Config;
        if (config.Count == 0) return true; // nothing configured, keep vanilla behavior

        try
        {
            var areas = __instance.areaHazeburnts;
            if (areas == null) return true;

            for (var i = 0; i < areas.Count; i++) areas[i].Clear();

            foreach (var entry in config)
            {
                var area = entry.Key;
                if (area < 1 || area > MaxArea || area >= areas.Count) continue;

                foreach (var name in entry.Value)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!MonsterCatalog.indexLookup.TryGetValue(name, out var idx)) continue;
                    if (idx < 0 || idx >= MonsterCatalog.monsterDef.Count) continue;

                    var def = MonsterCatalog.monsterDef[idx];
                    if (def.type != 1) continue; // only monsters can spawn through this system

                    // Apply the hazeburnt behavior to monsters that do not have it in vanilla.
                    if (def.gameMonster != null) def.gameMonster.hazeBurnt = true;

                    if (!areas[area].Contains(idx)) areas[area].Add(idx);
                }
            }

            Plugin.Instance.Log.LogInfo("[HazeburntSpawn] Rebuilt hazeburnt spawn pools from config.");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogError($"[HazeburntSpawn] Failed to apply spawn lists: {ex}");
            return true;
        }
    }
}
