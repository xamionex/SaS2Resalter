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

[HarmonyPatch]
public static class MagicDamagePatch
{
    private static string ConfigPath => Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "magic_damage.json");

    private static Dictionary<string, float[]> _overrides;
    private static long _lastFileTime;

    // Cache: owner ID -> (weaponName, slotIndex)
    private static readonly Dictionary<int, string> CachedWeaponNames = new();
    private static readonly Dictionary<int, int>    CachedSlots       = new();

    public static void ReloadConfig()
    {
        _overrides = null;
    }

    private static Dictionary<string, float[]> Overrides
    {
        get
        {
            if (!File.Exists(ConfigPath)) return _overrides ??= new Dictionary<string, float[]>();

            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_overrides != null && _lastFileTime == mtime) return _overrides;

            try
            {
                var json = File.ReadAllText(ConfigPath);
                _overrides = SimpleJson.ParseWeaponSlots(json);
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[MagicDamagePatch] Loaded {_overrides.Count} weapon overrides.");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[MagicDamagePatch] Config error: {ex.Message}");
                _overrides = new Dictionary<string, float[]>();
            }
            return _overrides;
        }
    }

    private static float GetDamage(string weaponName, int slotIdx)
    {
        if (!Overrides.TryGetValue(weaponName, out var slots)) return 0f;
        return slotIdx >= 0 && slotIdx < slots.Length ? slots[slotIdx] : 0f;
    }

    private static LootDef GetEquippedWeaponDef(Player player)
    {
        try
        {
            var equipment = player.equipment;
            if (equipment == null) return null;

            var weaponSlotIdx = equipment.GetWeaponSlotIdx();
            if (weaponSlotIdx < 0 || weaponSlotIdx >= equipment.equippedItem.Length)
                return null;

            var invIdx = equipment.equippedItem[weaponSlotIdx];
            if (invIdx < 0 || invIdx >= equipment.invItem.Count)
                return null;

            var playerItem = equipment.invItem[invIdx];
            if (playerItem.lootIdx < 0 || playerItem.lootIdx >= LootCatalog.lootDef.Count)
                return null;

            return LootCatalog.lootDef[playerItem.lootIdx];
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[MagicDamagePatch] GetEquippedWeaponDef error: {ex.Message}");
            return null;
        }
    }

    // 1. Cache the weapon and slot when the runic art is activated
    [HarmonyPatch(typeof(RunicAttack), nameof(RunicAttack.Init))]
    [HarmonyPrefix]
    private static void RunicInit_Pre(Particle p,
        Vector2 loc, Vector2 traj, float size, float rotation,
        int flags, int aux, int owner)
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

        CachedWeaponNames[owner] = weapDef.name;
        CachedSlots[owner]       = slot;

        Plugin.Instance.Log.LogInfo(
            $"[MagicDamagePatch] Cached: owner={owner} weapon={weapDef.name} slot={slot}");
    }

    // 2. Apply the cache to the first non‑melee hit after the cast
    [HarmonyPatch(typeof(HitManager), "PopulateHVals")]
    [HarmonyPrefix]
    private static bool Prefix_PopulateHVals(Particle p)
    {
        if (p.owner < 0) return true;

        if (!CachedWeaponNames.TryGetValue(p.owner, out var weaponName))
            return true;

        // If this hit is a melee attack, do NOT consume the cache, leave it for the projectile.
        if (ParticleCatalog.particle[p.type].IsMeleeHitTest())
            return true;   // melee swing, skip override, keep cache

        var slot   = CachedSlots[p.owner];
        var dmg = GetDamage(weaponName, slot);
        if (dmg <= 0f) return true;

        // Set base physical damage, the game will multiply by stats and buffs
        p.hVals[0] = dmg;
        for (var i = 1; i < p.hVals.Length; i++)
            p.hVals[i] = 0f;
        p.hasHVals = true;

        // Clear the cache so the override is only used once
        CachedWeaponNames.Remove(p.owner);
        CachedSlots.Remove(p.owner);

        Plugin.Instance.Log.LogInfo($"[MagicDamagePatch] Override applied: weapon={weaponName} slot={slot} base dmg={dmg} (type={p.type})");

        return false; // skip original PopulateHVals
    }
}