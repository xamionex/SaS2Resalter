using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Bestiary.monsters;
using HarmonyLib;

namespace SaS2Resalter;

/// <summary>
/// Applies per-monster, per-field multipliers to monster float fields.
///
/// The game copies the catalog's float fields into the live GameMonster at read time (MonsterDef.Read -> GameMonster.ProcessFlags:
/// Hover Speed, Hover Accel, Run speed, Poise, ...) and reads the rest on demand via GameMonster.GetFieldFloat.
/// Multiplying the field value right after it is read from the file covers every consumer, including the movement fields cached in ProcessFlags.
///
/// Config: BepInEx/config/amione.SaS2Resalter/monster_field_mults.json
/// <code>
/// {
///   "ash_imp": { "42": 1.5, "43": 0.5 },
///   "bargeling": { "9": 2.0 }
/// }
/// </code>
/// Keys are monster names, values map field id to a multiplier (1.0 = unchanged, 0.5 = half, 2.0 = double). 0 / negative are treated as unchanged.
/// Written by the editor on apply.
/// </summary>
[HarmonyPatch]
public static class MonsterFieldMultPatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "monster_field_mults.json");

    private static Dictionary<string, Dictionary<int, float>> _mults;
    private static long _lastFileTime;

    public static void ReloadConfig() => _mults = null;

    private static Dictionary<string, Dictionary<int, float>> Mults
    {
        get
        {
            if (!File.Exists(ConfigPath)) return _mults ??= new Dictionary<string, Dictionary<int, float>>();
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_mults != null && _lastFileTime == mtime) return _mults;
            try
            {
                _mults = SimpleJson.ParseMonsterFieldMults(File.ReadAllText(ConfigPath));
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[MonsterFieldMult] Loaded {_mults.Count} monster override(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[MonsterFieldMult] Config error: {ex.Message}");
                _mults = new Dictionary<string, Dictionary<int, float>>();
            }

            return _mults;
        }
    }

    /// The monster whose fields are currently being read (set by the MonsterDef.Read prefix).
    private static string _currentMonster;

    /// Peek the monster name at the start of MonsterDef.Read without consuming the stream.
    /// The name is the first string in the def, so the prefix reads it and rewinds.
    /// MonsterDef.Read is internal, so it is patched by name (AccessTools resolves it).
    [HarmonyPatch(typeof(MonsterDef), "Read", typeof(BinaryReader))]
    [HarmonyPrefix]
    private static void MonsterDefRead_Pre(BinaryReader reader)
    {
        try
        {
            var pos = reader.BaseStream.Position;
            _currentMonster = reader.ReadString();
            reader.BaseStream.Position = pos;
        }
        catch
        {
            _currentMonster = null;
        }
    }

    /// Multiply a float field right after it is read, when the current monster has an override.
    [HarmonyPatch(typeof(MonsterField), nameof(MonsterField.Read), typeof(BinaryReader))]
    [HarmonyPostfix]
    private static void MonsterFieldRead_Post(MonsterField __instance)
    {
        try
        {
            if (__instance is not { dataType: MonsterField.DATA_TYPE_FLOAT }) return;
            if (_currentMonster == null) return;
            if (!Mults.TryGetValue(_currentMonster, out var fields)) return;
            if (!fields.TryGetValue(__instance.ID, out var mul)) return;
            if (mul <= 0f) return; // 0 / negative = not configured
            __instance.fData *= mul;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[MonsterFieldMult] Failed to apply: {ex.Message}");
        }
    }
}
