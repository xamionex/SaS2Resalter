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
    // Min/max rolls are cached per field so the value stays stable between config reloads
    // instead of re-rolling on every GetArtifactVal call (which would flicker stats like MaxHP).
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
    public static float GetBoostValue(int field)
    {
        if (!Boosts.TryGetValue(field.ToString(), out var b)) return -1f;
        if (b.Length < 4) return -1f;
        if (b[2] > 0.5f) return b[3]; // static
        var min = b[0];
        var max = b[1];
        if (max <= min) return min;
        if (_rolledValues.TryGetValue(field, out var cached)) return cached;
        var rolled = min + (float)_rand.NextDouble() * (max - min);
        _rolledValues[field] = rolled;
        return rolled;
    }

    [HarmonyPatch(typeof(PlayerEquipment), "GetArtifactVal")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void GetArtifactVal_Postfix(int field, ref float __result)
    {
        try
        {
            // Only override when the player actually has an artifact equipped: vanilla returns 0
            // when no equipped artifact contributes the field, and the override must not leak
            // into stats for artifacts that are merely owned or in the shop preview.
            if (__result <= 0f) return;
            var v = GetBoostValue(field);
            if (v < 0f) return; // no override, keep the original
            __result = v;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[ArtifactBoosts] Failed to apply: {ex.Message}");
        }
    }

    /// The artifact tooltip (PlayerArtifactData.GetDesc) prints the raw rolled value[] array,
    /// which does not reflect the overrides. This postfix rewrites each stat line to the
    /// configured value so the UI shows the boosted numbers.
    [HarmonyPatch(typeof(PlayerArtifactData), "GetDesc")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void GetDesc_Postfix(PlayerArtifactData __instance, ref string __result)
    {
        try
        {
            if (__result == null || __instance == null || __instance.value == null) return;
            var lines = __result.Split('\n');
            // GetDesc prints header lines (tier, rarity) first, then one line per nonzero
            // field in field order. Match each stat line to the next nonzero field.
            var lineIdx = 0;
            var field = 0;
            while (lineIdx < lines.Length && field < __instance.value.Length)
            {
                var line = lines[lineIdx];
                // Stat lines start with "+" or "-" (field 16 is a reduction).
                if (line.Length >= 2 && (line[0] == '+' || line[0] == '-'))
                {
                    while (field < __instance.value.Length && __instance.value[field] <= 0f) field++;
                    if (field >= __instance.value.Length) break;
                    var v = GetBoostValue(field);
                    if (v >= 0f)
                    {
                        var end = 1;
                        while (end < line.Length && (char.IsDigit(line[end]) || line[end] == '.')) end++;
                        lines[lineIdx] = line[0] + v.ToString("0.0") + line.Substring(end);
                    }
                    field++;
                }
                lineIdx++;
            }
            __result = string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[ArtifactBoosts] Tooltip failed: {ex.Message}");
        }
    }
}
