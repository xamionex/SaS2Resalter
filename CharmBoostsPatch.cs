using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Chronicler.dialog;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.player;

namespace SaS2Resalter;

/// <summary>
/// Overrides talisman (charm) boost values per flag.
///
/// The game computes a charm boost's size in PlayerEquipment.GetCharmVal(int flag):
/// it counts how many equipped talismans share the flag and returns a tier (1.0 / 1.25 / 1.35 / 1.4), which every stat formula then multiplies by its own hardcoded factor.
/// This patch replaces that tier with the configured value: either a roll between Min and Max, or the Static Boost value when Static is set.
/// Values are actual in-game magnitudes (10 = 10%)
/// The patch divides by the vanilla magnitude per flag to recover the scalar the stat formulas expect.
///
/// Config: BepInEx/config/amione.SaS2Resalter/charm_boosts.json
/// <code>
/// {
///   "0":  { "min": 10.0, "max": 20.0, "static_boost": false, "static_value": 10.0 },
///   "14": { "min": 10.0, "max": 10.0, "static_boost": true,  "static_value": 25.0 }
/// }
/// </code>
/// Omitted flags keep the vanilla behavior. Written by the editor on apply.
/// </summary>
[HarmonyPatch]
public static class CharmBoostsPatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "charm_boosts.json");

    private static Dictionary<string, float[]> _boosts;
    private static long _lastFileTime;
    private static readonly Random _rand = new();
    // Min/max rolls are cached per flag so the value stays stable between config reloads
    // instead of re-rolling on every GetCharmVal call (which would flicker stats like MaxHP).
    private static readonly Dictionary<int, float> _rolledValues = new();

    public static void ReloadConfig()
    {
        _boosts = null;
        _rolledValues.Clear();
    }

    private static Dictionary<string, float[]> Boosts
    {
        get
        {
            if (!File.Exists(ConfigPath)) return _boosts ??= new Dictionary<string, float[]>();
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_boosts != null && _lastFileTime == mtime) return _boosts;
            try
            {
                _boosts = SimpleJson.ParseCharmBoosts(File.ReadAllText(ConfigPath));
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[CharmBoosts] Loaded {_boosts.Count} boost override(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[CharmBoosts] Config error: {ex.Message}");
                _boosts = new Dictionary<string, float[]>();
            }

            return _boosts;
        }
    }

    /// Vanilla magnitude per charm flag (mirrors the editor's table).
    /// The configured actual values are divided by this to recover the GetCharmVal scalar the stat formulas multiply.
    private static float VanillaValue(int flag)
    {
        switch (flag)
        {
            case 0: return 10f;   // Phys Def
            case 1: case 2: case 3: case 4: case 5: return 20f; // Elemental Def
            case 6: return 10f;   // Item Find
            case 7: return 0.15f; // Rage Gain
            case 8: return 1f;    // Rage Window
            case 11: return 2f;   // Fast grapple/climb
            case 12: return 10f;  // Stamina Regen
            case 13: return 50f;  // Silver Find
            case 14: return 10f;  // Damage
            case 15: return 5f;   // Gold
            case 16: case 17: case 18: case 19: case 20: return 20f; // Elemental Atk
            case 29: return 5f;   // Carry Weight
            case 30: case 31: return 5f;   // HP/MP Kill Gain
            case 32: return 50f;  // Parry Stagger Damage
            case 33: return 25f;  // MP Regain
            case 34: return 50f;  // Riposte Dmg
            case 35: return 50f;  // Dying Boost
            case 36: case 37: case 39: return 5f;  // Max HP/Rage/Stamina Boost
            case 38: return 10f;  // Max MP Boost
            case 40: case 41: return 2.5f;  // MP/HP Parry regain
            case 42: case 43: return 50f;   // MP/HP Riposte regain
            case 44: return 50f;  // Restock speed
            case 45: case 46: return 12.5f; // Rage Parry/Riposte regain
            case 48: return 10f;  // Blocking stamina cheap
            case 49: return 15f;  // Runic art boost
            case 50: return 50f;  // Faster Drinking
            case 51: return 3.1f; // Overall defense
            case 52: case 53: return 10f; // Haze HP/MP
            case 54: return 3f;   // Haze Rage
            default: return 1f;   // boolean/no-magnitude flags
        }
    }

    /// Returns the GetCharmVal scalar for a flag, or -1 if not overridden.
    private static float GetBoostScalar(int flag)
    {
        if (!Boosts.TryGetValue(flag.ToString(), out var b)) return -1f;
        if (b.Length < 4) return -1f;
        var vanilla = VanillaValue(flag);
        if (vanilla <= 0f) return -1f;

        float value;
        if (b[2] > 0.5f)
        {
            value = b[3]; // static
        }
        else
        {
            var min = b[0];
            var max = b[1];
            if (max <= min)
            {
                value = min;
            }
            else if (!_rolledValues.TryGetValue(flag, out value))
            {
                value = min + (float)_rand.NextDouble() * (max - min);
                _rolledValues[flag] = value;
            }
        }
        return value / vanilla;
    }

    /// The configured actual magnitude for a flag, or -1 if not overridden.
    /// This is the value the tooltip should show (the scalar times the vanilla magnitude).
    public static float GetBoostValue(int flag)
    {
        var scalar = GetBoostScalar(flag);
        return scalar < 0f ? -1f : scalar * VanillaValue(flag);
    }

    /// The localized string index the vanilla charm tooltip uses for each flag, or -1 if the flag has no tooltip line.
    private static int GetFlagLocStrIdx(int flag)
    {
        switch (flag)
        {
            case 0: return 508;
            case 1: return 511;
            case 2: return 502;
            case 3: return 533;
            case 4: return 522;
            case 5: return 506;
            case 6: return 521;
            case 7: return 513;
            case 8: return 514;
            case 10: return 532;
            case 11: return 545;
            case 12: return 548;
            case 13: return 543;
            case 14: return 505;
            case 15: return 515;
            case 16: return 512;
            case 17: return 503;
            case 18: return 534;
            case 19: return 523;
            case 20: return 507;
            case 29: return 500;
            case 30: return 520;
            case 31: return 525;
            case 32: return 528;
            case 33: return 526;
            case 34: return 538;
            case 35: return 509;
            case 36: return 519;
            case 37: return 535;
            case 38: return 524;
            case 39: return 544;
            case 40: return 530;
            case 41: return 529;
            case 42: return 540;
            case 43: return 539;
            case 44: return 536;
            case 45: return 531;
            case 46: return 541;
            case 47: return 547;
            case 48: return 499;
            case 49: return 542;
            case 50: return 510;
            case 51: return 527;
            case 52: return 516;
            case 53: return 517;
            case 54: return 518;
            default: return -1;
        }
    }

    [HarmonyPatch(typeof(PlayerEquipment), "GetCharmVal")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void GetCharmVal_Postfix(PlayerEquipment __instance, int flag, ref float __result)
    {
        try
        {
            // Only override when the player actually has the flag equipped: vanilla returns 0
            // when no equipped talisman carries it, and the override must not leak into stats
            // for charms that are merely owned or in the shop preview.
            if (__result <= 0f) return;
            var scalar = GetBoostScalar(flag);
            if (scalar < 0f) return; // no override, keep the original
            __result = scalar;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[CharmBoosts] Failed to apply: {ex.Message}");
        }
    }

    /// Append the configured magnitude to each flag line of the charm tooltip, so the UI shows
    /// the boosted values instead of only the flag names. Flag names come from the game's
    /// localized string table (the same entries the vanilla tooltip uses).
    [HarmonyPatch(typeof(PlayerItem), "GetCharmDesc")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void GetCharmDesc_Postfix(PlayerItem __instance, LootDef lDef, ref string __result)
    {
        try
        {
            if (__result == null || lDef == null || lDef.flags == null || lDef.flags.Count == 0) return;
            var sb = new StringBuilder(__result);
            foreach (var flag in lDef.flags)
            {
                var v = GetBoostValue(flag);
                if (v < 0f) continue;
                var locIdx = GetFlagLocStrIdx(flag);
                if (locIdx < 0) continue;
                var name = LocStrings.GetLocStr(locIdx);
                var unit = flag >= 13 && flag <= 20 || flag >= 30 && flag <= 37 || flag >= 39 && flag <= 46 || flag >= 48 && flag <= 50 || flag >= 52 && flag <= 53 ? "%" : "";
                sb.Append("\r\n");
                sb.Append(name);
                sb.Append(": +");
                sb.Append(v.ToString("0.0"));
                sb.Append(unit);
            }
            __result = sb.ToString();
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[CharmBoosts] Tooltip failed: {ex.Message}");
        }
    }
}
