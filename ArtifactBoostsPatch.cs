using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using ProjectMage.player;

namespace SaS2Resalter;

/// <summary>
/// Overrides artifact (talisman subtype 3/4/5) stat values per field.
///
/// Equipped artifacts contribute 35 percentage values consumed via PlayerEquipment.GetArtifactVal(int field).
/// Normally the values are rolled when the artifact is obtained (PlayerArtifactData.Populate), this patch replaces the rolled value with the configured value:
/// either a roll between Min and Max, or the Static Boost value when Static is set.
/// Values are percentages (5 = 5%).
///
/// Config: BepInEx/config/amione.SaS2Resalter/artifact_boosts.json
/// <code>
/// {
///   "4":  { "min": 5.0, "max": 40.0, "static_boost": false, "static_value": 5.0 },
///   "0":  { "min": 10.0, "max": 10.0, "static_boost": true,  "static_value": 25.0 }
/// }
/// </code>
/// Omitted fields keep the vanilla rolled value. Written by the editor on apply.
/// </summary>
[HarmonyPatch]
public static class ArtifactBoostsPatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "artifact_boosts.json");

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
                Plugin.Instance.Log.LogInfo($"[ArtifactBoosts] Loaded {_boosts.Count} field override(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[ArtifactBoosts] Config error: {ex.Message}");
                _boosts = new Dictionary<string, float[]>();
            }

            return _boosts;
        }
    }

    /// Returns the configured value for a field, or -1 if not overridden.
    private static float GetBoostValue(int field)
    {
        if (!Boosts.TryGetValue(field.ToString(), out var b)) return -1f;
        if (b.Length < 4) return -1f;
        if (b[2] > 0.5f) return b[3]; // static
        var min = b[0];
        var max = b[1];
        return max <= min ? min : min + (float)_rand.NextDouble() * (max - min);
    }

    [HarmonyPatch(typeof(PlayerEquipment), "GetArtifactVal")]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static bool GetArtifactVal_Prefix(int field, ref float __result)
    {
        try
        {
            var v = GetBoostValue(field);
            if (v < 0f) return true; // no override, let the original run
            __result = v;
            return false; // skip the original
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[ArtifactBoosts] Failed to apply: {ex.Message}");
            return true;
        }
    }
}
