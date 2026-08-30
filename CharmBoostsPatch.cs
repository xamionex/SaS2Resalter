using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
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

    public static void ReloadConfig() => _boosts = null;

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
            value = max <= min ? min : min + (float)_rand.NextDouble() * (max - min);
        }
        return value / vanilla;
    }

    [HarmonyPatch(typeof(PlayerEquipment), "GetCharmVal")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool GetCharmVal_Prefix(int flag, ref float __result)
    {
        try
        {
            var scalar = GetBoostScalar(flag);
            if (scalar < 0f) return true; // no override, let the original run
            __result = scalar;
            return false; // skip the original
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[CharmBoosts] Failed to apply: {ex.Message}");
            return true;
        }
    }
}
