using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using ProjectMage.player.menu.levels;

namespace SaS2Resalter;

/// <summary>
/// Makes custom (and any chosen) items obtainable by injecting them into merchant buy lists.
///
/// Shops in this game are driven entirely by NPC dialog "store scripts" (a list of loot names),
/// passed to LevelBuySell.Activate(string[] buy, bool sell). There is no per-item "sold here" flag
/// and no enumerable shop list, so we append a configured set of entries to every buy menu.
///
/// Config: BepInEx/config/amione.SaS2Resalter/shop_additions.txt
///   One entry per line. Each entry is either:
///     item_name            (always for sale)
///     flag_name:item_name  (only for sale once the player has flag_name, matching the game's
///                           own "flag:item" store-script syntax)
///   Blank lines and lines starting with '#' are ignored.
///
/// The editor writes this file when items are marked "Sell in shops".
/// </summary>
[HarmonyPatch]
public static class ShopAdditionsPatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "shop_additions.txt");

    private static List<string> _additions;
    private static long _lastFileTime;

    public static void ReloadConfig() => _additions = null;

    private static List<string> Additions
    {
        get
        {
            if (!File.Exists(ConfigPath)) return _additions ??= new List<string>();
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_additions != null && _lastFileTime == mtime) return _additions;
            try
            {
                var list = new List<string>();
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    list.Add(line);
                }

                _additions = list;
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[ShopAdditions] Loaded {list.Count} shop addition(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[ShopAdditions] Config error: {ex.Message}");
                _additions = new List<string>();
            }

            return _additions;
        }
    }

    [HarmonyPatch(typeof(LevelBuySell), nameof(LevelBuySell.Activate))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static void Activate_Prefix(ref string[] buy, bool sell)
    {
        try
        {
            // Only touch real buy menus, never the sell screen or empty/special invocations.
            if (sell || buy == null || buy.Length == 0) return;
            if (buy[0].StartsWith("sell")) return;

            var adds = Additions;
            if (adds.Count == 0) return;

            var list = new List<string>(buy);
            foreach (var a in adds)
                if (!list.Contains(a))
                    list.Add(a);

            buy = list.ToArray();
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[ShopAdditions] Failed to inject: {ex.Message}");
        }
    }
}
