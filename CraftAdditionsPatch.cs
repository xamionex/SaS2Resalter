using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.player.menu.levels;

namespace SaS2Resalter;

/// <summary>
/// Makes chosen items appear in the craft / equipment menu, independently of shops.
///
/// The crafting list is built from the loot catalog: an item appears only if LevelCrafting's
/// private GetCraftingMats(lDef) returns a non-null material list, which it does only when the
/// item has crafting-material fields AND the player already owns at least one of them. That hides
/// any item the player can't currently craft, and gives recipe-less (e.g. newly created) items no
/// way in.
///
/// This postfix, for items listed in craft_additions.txt, ensures a non-null recipe:
///   - if the item already has crafting-material fields, return them (ignoring the ownership gate
///     so it is always visible);
///   - otherwise, if a material was configured ("material:item"), return that single material
///     (count comes from the item's fields, which is 0 for a blank item = free craft).
///
/// Config: BepInEx/config/amione.SaS2Resalter/craft_additions.txt
///   One entry per line: "item" or "material:item". Written by the editor's
///   "Add to craft / equipment menu" toggle.
/// </summary>
[HarmonyPatch]
public static class CraftAdditionsPatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "craft_additions.txt");

    // item name -> configured material ("" if none)
    private static Dictionary<string, string> _additions;
    private static long _lastFileTime;

    public static void ReloadConfig() => _additions = null;

    private static Dictionary<string, string> Additions
    {
        get
        {
            if (!File.Exists(ConfigPath)) return _additions ??= new Dictionary<string, string>();
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_additions != null && _lastFileTime == mtime) return _additions;
            try
            {
                var map = new Dictionary<string, string>();
                foreach (var raw in File.ReadAllLines(ConfigPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var colon = line.IndexOf(':');
                    if (colon >= 0)
                        map[line.Substring(colon + 1)] = line.Substring(0, colon);
                    else
                        map[line] = string.Empty;
                }

                _additions = map;
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[CraftAdditions] Loaded {map.Count} craft addition(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[CraftAdditions] Config error: {ex.Message}");
                _additions = new Dictionary<string, string>();
            }

            return _additions;
        }
    }

    /// The lootField indices that hold crafting-material names, per loot type (mirrors the game's
    /// GetCraftingMats). Anything else has no craftable recipe.
    private static int[] MatFieldIndices(int type)
    {
        switch (type)
        {
            case 0: return new[] { 7, 9, 11 };
            case 1:
            case 2: return new[] { 2, 4, 6 };
            case 6: return new[] { 0, 2, 4 };
            default: return new int[0];
        }
    }

    private static List<string> RawMats(LootDef lDef)
    {
        var list = new List<string>();
        foreach (var idx in MatFieldIndices(lDef.type))
        {
            if (lDef.lootField == null || idx >= lDef.lootField.Count) continue;
            var mat = lDef.lootField[idx].strData;
            if (!string.IsNullOrEmpty(mat)) list.Add(mat);
        }

        return list;
    }

    [HarmonyPatch(typeof(LevelCrafting), "GetCraftingMats")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void GetCraftingMats_Postfix(LootDef lDef, ref List<string> __result)
    {
        try
        {
            if (lDef == null) return;
            if (__result != null && __result.Count > 0) return; // already craftable, leave it
            if (!Additions.TryGetValue(lDef.name, out var material)) return;

            // Prefer the item's own recipe (ignoring the ownership gate so it is always visible).
            var raw = RawMats(lDef);
            if (raw.Count > 0)
            {
                __result = raw;
                return;
            }

            // No recipe on the item: fabricate one from the configured material if provided.
            if (!string.IsNullOrEmpty(material))
                __result = new List<string> { material };
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[CraftAdditions] Failed: {ex.Message}");
        }
    }
}
