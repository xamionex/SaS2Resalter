using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.player;

namespace SaS2Resalter;

/// <summary>
/// Applies per-weapon, per-slot multipliers to runic art MANA/RAGE cost and COOLDOWN.
///
/// Vanilla derives both values from the rune (magicDef), not the weapon:
///   cost     = magicDef.lootField[0].fData
///   cooldown = magicDef.lootField[3].iData
/// so the same rune costs the same and shares a cooldown on every weapon. These
/// overrides re-key cost and cooldown to the equipped weapon + cast slot, matching
/// how MagicDamagePatch re-keys damage.
///
/// Configs (multipliers; 1.0 = vanilla, omitted or &lt;= 0 = unchanged):
///   BepInEx/config/amione.SaS2Resalter/magic_cost.json
///   BepInEx/config/amione.SaS2Resalter/magic_cooldown.json
/// <code>
/// {
///   "iron_sword":  { "x": 0.5, "y": 1.0, "b": 2.0 },
///   "chaos_blade": { "x": 1.0, "y": 0.75, "b": 1.0 }
/// }
/// </code>
/// Slots: x = slot 0 (attack), y = slot 1 (strong), b = slot 2 (use).
///
/// Both values come from the same GetRunicAttackCost overload, which the casting
/// code (RunicAttack.Init) and the affordability/cooldown gate (PlayerEquipmentAnim)
/// both resolve through, so a single postfix keeps gameplay, cost deduction and the
/// gate consistent.
/// </summary>
[HarmonyPatch]
public static class MagicCostPatch
{
    /// Loads and hot-reloads a per-weapon, per-slot multiplier table from a JSON file
    /// in the Resalter config directory. Mirrors the loader in MagicDamagePatch.
    private sealed class WeaponSlotConfig
    {
        private readonly string _fileName;
        private Dictionary<string, float[]> _overrides;
        private long _lastFileTime;

        public WeaponSlotConfig(string fileName) => _fileName = fileName;

        private string Path => System.IO.Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", _fileName);

        private Dictionary<string, float[]> Overrides
        {
            get
            {
                if (!File.Exists(Path)) return _overrides ??= new Dictionary<string, float[]>();
                var mtime = new FileInfo(Path).LastWriteTime.Ticks;
                if (_overrides != null && _lastFileTime == mtime) return _overrides;
                try
                {
                    _overrides = SimpleJson.ParseWeaponSlots(File.ReadAllText(Path));
                    _lastFileTime = mtime;
                    Plugin.Instance.Log.LogInfo(
                        $"[MagicCostPatch] Loaded {_overrides.Count} override(s) from {_fileName}.");
                }
                catch (Exception ex)
                {
                    Plugin.Instance.Log.LogError($"[MagicCostPatch] Config error ({_fileName}): {ex.Message}");
                    _overrides = new Dictionary<string, float[]>();
                }

                return _overrides;
            }
        }

        public float GetMultiplier(string weaponName, int slotIdx)
        {
            if (weaponName == null) return 1f;
            if (!Overrides.TryGetValue(weaponName, out var slots)) return 1f;
            var mul = slotIdx >= 0 && slotIdx < slots.Length ? slots[slotIdx] : 1f;
            // Treat 0 / negative as "not configured" (consistent with MagicDamagePatch).
            return mul <= 0f ? 1f : mul;
        }
    }

    private static readonly WeaponSlotConfig CostConfig = new("magic_cost.json");
    private static readonly WeaponSlotConfig CooldownConfig = new("magic_cooldown.json");

    /// Multiply the returned cost and the ref cooldown by the per-weapon, per-slot factors.
    /// Both configs hot-reload on file change (mtime check in the loader).
    [HarmonyPatch(typeof(PlayerEquipment), nameof(PlayerEquipment.GetRunicAttackCost),
        new[]
        {
            typeof(LootDef), typeof(LootDef), typeof(int),
            typeof(bool), typeof(bool), typeof(float)
        },
        new[]
        {
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Ref
        })]
    [HarmonyPostfix]
    private static void GetRunicAttackCost_Post(LootDef weapDef, int chargeIdx, ref float cooldown, ref float __result)
    {
        if (weapDef == null) return;
        if (chargeIdx is < 0 or > 2) return;

        var costMul = CostConfig.GetMultiplier(weapDef.name, chargeIdx);
        if (Math.Abs(costMul - 1f) > 0.0001f)
            __result *= costMul;

        var cooldownMul = CooldownConfig.GetMultiplier(weapDef.name, chargeIdx);
        if (Math.Abs(cooldownMul - 1f) > 0.0001f)
            cooldown *= cooldownMul;
    }
}
