using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Common;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.hit;
using ProjectMage.particles;
using ProjectMage.particles.particles.runic;
using ProjectMage.player;

namespace SaS2Resalter;

/// <summary>
/// Applies a per-weapon, per-slot damage multiplier to all runic art hits.
///
/// Config: BepInEx/config/amione.SaS2Resalter/magic_damage.json
/// <code>
/// {
///   "iron_sword":  { "x": 1.5, "y": 1.0,  "b": 2.0 },
///   "chaos_blade": { "x": 2.5, "y": 0.75, "b": 1.0 }
/// }
/// </code>
/// Values are multipliers (1.0 = no change, 1.5 = +50 %, 0.5 = -50 %).
/// Omitted slots default to 1.0.
///
/// How it works:
///   1. RunicAttack.Init prefix  -> records which weapon + slot fired (per owner ID).
///   2. PopulateHVals postfix    -> after the game fills in stat-scaled physical and elemental hVals, we multiply every index by the configured factor. No values are replaced or zeroed.
///   3. The per-owner cache is NOT cleared here; it stays valid for every projectile spawned by the same cast (Seekers, Wave, Column, etc.) and is only overwritten when the owner fires another runic art.
/// </summary>
[HarmonyPatch]
public static class MagicDamagePatch
{
    private static string ConfigPath => Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "magic_damage.json");
    private static Dictionary<string, float[]> _overrides;
    private static long _lastFileTime;
    public static void ReloadConfig() => _overrides = null;

    private static Dictionary<string, float[]> Overrides
    {
        get
        {
            if (!File.Exists(ConfigPath)) return _overrides ??= new Dictionary<string, float[]>();
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_overrides != null && _lastFileTime == mtime) return _overrides;
            try
            {
                _overrides = SimpleJson.ParseWeaponSlots(File.ReadAllText(ConfigPath));
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[MagicDamagePatch] Loaded {_overrides.Count} weapon override(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[MagicDamagePatch] Config error: {ex.Message}");
                _overrides = new Dictionary<string, float[]>();
            }

            return _overrides;
        }
    }

    /// Returns the multiplier for a given weapon + slot, or 1.0 if no override exists.
    private static float GetMultiplier(string weaponName, int slotIdx)
    {
        if (!Overrides.TryGetValue(weaponName, out var slots)) return 1f;
        var mul = slotIdx >= 0 && slotIdx < slots.Length ? slots[slotIdx] : 1f;
        // Treat 0 as "not configured" (backwards-compat with old flat-value configs)
        return mul <= 0f ? 1f : mul;
    }

    // Per-owner cast cache
    private static readonly Dictionary<int, string> CachedWeaponNames = new();
    private static readonly Dictionary<int, int> CachedSlots = new();

    private static LootDef GetEquippedWeaponDef(Player player)
    {
        try
        {
            var eq = player.equipment;
            if (eq == null) return null;
            var wSlot = eq.GetWeaponSlotIdx();
            if (wSlot < 0 || wSlot >= eq.equippedItem.Length) return null;
            var invIdx = eq.equippedItem[wSlot];
            if (invIdx < 0 || invIdx >= eq.invItem.Count) return null;
            var lootIdx = eq.invItem[invIdx].lootIdx;
            if (lootIdx < 0 || lootIdx >= LootCatalog.lootDef.Count) return null;
            return LootCatalog.lootDef[lootIdx];
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[MagicDamagePatch] GetEquippedWeaponDef error: {ex.Message}");
            return null;
        }
    }

    /// record weapon + slot on cast
    [HarmonyPatch(typeof(RunicAttack), nameof(RunicAttack.Init))]
    [HarmonyPrefix]
    private static void RunicInit_Pre(Particle p, Vector2 loc, Vector2 traj, float size, float rotation, int flags,
        int aux, int owner)
    {
        if (owner < 0) return;
        var c = ProjectMage.character.CharMgr.character[owner];
        if (!c.exists || c.playerIdx < 0) return;
        var player = c.GetPlayer();
        if (player == null) return;
        var weapDef = GetEquippedWeaponDef(player);
        if (weapDef == null) return;
        var slot = c.chargeIdx; // 0 = X, 1 = Y, 2 = B
        if (slot is < 0 or > 2) return;

        // Overwrite any previous entry for this owner, the new cast takes priority.
        CachedWeaponNames[owner] = weapDef.name;
        CachedSlots[owner] = slot;
        Plugin.Instance.Log.LogInfo(
            $"[MagicDamagePatch] Cast recorded: owner={owner} weapon={weapDef.name} slot={slot}");
    }

    /// multiply all hVals after the game fills them in
    ///
    /// Why Postfix?
    ///   - The original PopulateHVals applies stat scaling (STR/DEX/Arcana) and
    ///     splits damage across hVals[0] (physical) and hVals[1+] (elemental).
    ///   - Running after it means we multiply the fully-scaled result, preserving
    ///     all stat contributions and elemental fractions.
    ///
    /// Why not Remove() from the cache?
    ///   - A single runic cast can spawn many projectiles (Seekers, Wave, Column…).
    ///     Every one calls PopulateHVals on contact. Removing after the first hit
    ///     would leave the rest un-multiplied.
    ///   - The cache entry is overwritten when the next RunicAttack.Init fires,
    ///     so stale entries are impossible in practice.
    ///   - Melee hits are already excluded by IsMeleeHitTest(), so there is no
    ///     risk of leaking the multiplier onto a sword swing after the cast.
    [HarmonyPatch(typeof(HitManager), "PopulateHVals")]
    [HarmonyPostfix]
    private static void PopulateHValsPatch(Particle p)
    {
        if (p.owner < 0) return;
        if (!CachedWeaponNames.TryGetValue(p.owner, out var weaponName)) return;

        // Skip melee swings, their particle type fails IsMeleeHitTest.
        if (ParticleCatalog.particle[p.type].IsMeleeHitTest()) return;
        var slot = CachedSlots[p.owner];
        var mul = GetMultiplier(weaponName, slot);
        if (Math.Abs(mul - 1f) < 0.0001f) return; // nothing to do

        // Multiply physical (hVals[0]) AND every elemental component (hVals[1+]).
        for (var i = 0; i < p.hVals.Length; i++) p.hVals[i] *= mul;
        p.hasHVals = true;
        Plugin.Instance.Log.LogInfo(
            $"[MagicDamagePatch] ×{mul:F3} applied: owner={p.owner} weapon={weaponName} slot={slot} type={p.type}");
    }
}