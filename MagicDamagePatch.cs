using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Common;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.hit;
using ProjectMage.particles;
using ProjectMage.particles.particles;
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
///   3. The per-owner cache stays valid for every projectile spawned by the same cast (Seekers, Wave, Column, etc.), but only for a bounded window (CastMaxAgeMs) so the multiplier stops once the cast/buff lapses instead of leaking into the owner's normal damage until the next cast.
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
        return mul;
    }

    // Enchant-buff window cache (per owner). Only enchant magics populate this.
    private static readonly Dictionary<int, string> CachedWeaponNames = new();
    private static readonly Dictionary<int, int> CachedSlots = new();
    private static readonly Dictionary<int, int> CachedCastTick = new();

    /// How long an enchant's per-hit procs keep being scaled (matches the ~15s blade-enchant buff).
    private const int CastMaxAgeMs = 16000;

    // Synchronous "cast scope": set while a runic art's Init runs so the damage particles that
    // Init spawns (columns, waves, explosions, orbs, ...) get the multiplier, while the player's
    // normal/innate attacks (spawned outside the cast) do not.
    private static int _castOwner = -1;
    private static float _castMul = 1f;

    // Particles spawned during a cast scope, mapped to their multiplier. Keyed by reference; every
    // spawn (Particle.Init) re-tags or clears the slot, so the table stays bounded and pool-safe.
    // Direct-attack magics (e.g. the column) deal damage from HitManager.hVals rather than a cached
    // p.hVals, so they are scaled at the damage point (PopulateHVals) instead of at Init.
    private static readonly Dictionary<Particle, float> _taggedParticles = new();

    // While a tagged particle's Update runs, this holds its multiplier so any sub-particles it
    // spawns (seekers, turret shots, orbit bolts, ...) inherit the tag too.
    private static float _updatingTagMul;

    private struct CastState
    {
        public int Owner;
        public float Mul;
    }

    /// True when the owner's currently-charged runic art deals its damage through a temporary
    /// weapon buff (lootField[1].iData 0 or 1), i.e. a blade enchant. Those procs spawn later, so
    /// only enchants get a time-boxed window; direct-attack magics spawn synchronously instead.
    private static bool IsEnchantMagic(ProjectMage.character.Character c)
    {
        try
        {
            var idx = c.chargeLootIdx;
            if (idx < 0 || idx >= LootCatalog.lootDef.Count) return false;
            var def = LootCatalog.lootDef[idx];
            if (def.lootField == null || def.lootField.Count < 2) return false;
            var t = def.lootField[1].iData;
            return t == 0 || t == 1;
        }
        catch
        {
            return false;
        }
    }

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

    /// Open the synchronous cast scope (and, for enchant magics only, the buff window) so the
    /// art's own spawned particles get the multiplier. __state restores the previous scope after.
    [HarmonyPatch(typeof(RunicAttack), nameof(RunicAttack.Init))]
    [HarmonyPrefix]
    private static void RunicInit_Pre(int owner, out CastState __state)
    {
        __state = new CastState { Owner = _castOwner, Mul = _castMul };

        if (owner < 0) return;
        var c = ProjectMage.character.CharMgr.character[owner];
        if (!c.exists || c.playerIdx < 0) return;
        var player = c.GetPlayer();
        if (player == null) return;
        var weapDef = GetEquippedWeaponDef(player);
        if (weapDef == null) return;
        var slot = c.chargeIdx; // 0 = X, 1 = Y, 2 = B
        if (slot is < 0 or > 2) return;

        var mul = GetMultiplier(weapDef.name, slot);

        // Scope: particles spawned synchronously by this cast's Init get the multiplier.
        _castOwner = owner;
        _castMul = mul;

        // Only enchant magics get the lingering buff window (their damage is later weapon procs).
        // Direct-attack magics rely solely on the synchronous scope above, so they never leak the
        // multiplier into the player's normal damage. An already-running enchant window is left
        // alone (a later non-enchant cast must not cut it short).
        if (IsEnchantMagic(c))
        {
            CachedWeaponNames[owner] = weapDef.name;
            CachedSlots[owner] = slot;
            CachedCastTick[owner] = Environment.TickCount;
        }

        Plugin.Instance.Log.LogInfo(
            $"[MagicDamagePatch] Cast: owner={owner} weapon={weapDef.name} slot={slot} mul={mul} enchant={IsEnchantMagic(c)}");
    }

    /// Close the synchronous cast scope.
    [HarmonyPatch(typeof(RunicAttack), nameof(RunicAttack.Init))]
    [HarmonyPostfix]
    private static void RunicInit_Post(CastState __state)
    {
        _castOwner = __state.Owner;
        _castMul = __state.Mul;
    }

    /// Apply the per-weapon, per-slot multiplier ONCE, when the projectile is created and its
    /// hVals are first populated and cached on the particle (BaseParticle.Init).
    ///
    /// Why here and not on HitManager.PopulateHVals?
    ///   - For owner-stat particles the game caches the rolled damage in p.hVals and, on every
    ///     hit, copies p.hVals into the live HitManager.hVals (CalculateAndDealDamage). A postfix
    ///     on the per-hit PopulateHVals that multiplied p.hVals would therefore compound the
    ///     multiplier (mul^N) for any lingering projectile (Seekers, Wave, Column, beams), making
    ///     damage escalate wildly. Multiplying once at spawn applies the factor exactly one time
    ///     while still flowing through the per-hit p.hVals override.
    ///   - Init runs once per spawn (and again on pool reuse, which re-rolls p.hVals first), so
    ///     there is no compounding and no per-particle bookkeeping needed.
    ///   - Melee swings are excluded via IsMeleeHitTest, matching the previous behaviour.
    [HarmonyPatch(typeof(BaseParticle), "Init")]
    [HarmonyPostfix]
    private static void BaseParticleInit_Post(Particle p)
    {
        if (p == null || p.owner < 0 || !p.hasHVals) return;

        // Never touch melee swings (player's normal weapon damage).
        if (ParticleCatalog.particle[p.type].IsMeleeHitTest()) return;

        // Cast-spawned particles are handled by the tag path (ParticleInit_Post + the damage-time
        // hooks), so here we only scale enchant buff procs (per-hit, while a blade enchant is active).
        if (!CachedWeaponNames.TryGetValue(p.owner, out var weaponName)) return;

        if (CachedCastTick.TryGetValue(p.owner, out var castTick))
        {
            var ageMs = unchecked(Environment.TickCount - castTick);
            if (ageMs < 0 || ageMs > CastMaxAgeMs)
            {
                CachedWeaponNames.Remove(p.owner);
                CachedSlots.Remove(p.owner);
                CachedCastTick.Remove(p.owner);
                return;
            }
        }

        var slot = CachedSlots[p.owner];
        var mul = GetMultiplier(weaponName, slot);

        for (var i = 0; i < p.hVals.Length; i++) p.hVals[i] *= mul;
    }

    /// Tag (or clear) each particle as it spawns. Particle.Init is the single dispatch point for
    /// every spawn, so a particle created during a runic cast gets the cast's multiplier, and any
    /// particle reusing that pooled slot outside a cast has its tag cleared.
    [HarmonyPatch(typeof(Particle), nameof(Particle.Init))]
    [HarmonyPostfix]
    private static void ParticleInit_Post(Particle __instance)
    {
        if (__instance == null) return;
        if (_castOwner >= 0 && __instance.owner == _castOwner)
            _taggedParticles[__instance] = _castMul; // spawned during the cast itself
        else if (_updatingTagMul != 0f)
            _taggedParticles[__instance] = _updatingTagMul; // spawned by a tagged particle's Update
        else
            _taggedParticles.Remove(__instance);
    }

    /// Propagate a tagged particle's multiplier to whatever it spawns during its Update.
    [HarmonyPatch(typeof(Particle), nameof(Particle.Update))]
    [HarmonyPrefix]
    private static void ParticleUpdate_Pre(Particle __instance, out float __state)
    {
        __state = _updatingTagMul;
        _updatingTagMul = (__instance != null && _taggedParticles.TryGetValue(__instance, out var m))
            ? m
            : 0f;
    }

    [HarmonyPatch(typeof(Particle), nameof(Particle.Update))]
    [HarmonyPostfix]
    private static void ParticleUpdate_Post(float __state)
    {
        _updatingTagMul = __state;
    }

    /// Scale the per-hit damage of tagged (cast-spawned) particles. HitManager.hVals is the live,
    /// freshly-populated array used for this hit, so multiplying it here does not compound, and it
    /// covers particles (like the column) that deal damage from hVals rather than a cached p.hVals.
    [HarmonyPatch(typeof(HitManager), "PopulateHVals")]
    [HarmonyPostfix]
    // ReSharper disable once InconsistentNaming
    private static void PopulateHVals_Post(Particle p)
    {
        if (p == null || _taggedParticles.Count == 0) return;
        if (!_taggedParticles.TryGetValue(p, out var mul)) return;
        var hv = HitManager.hVals;
        if (hv == null) return;
        for (var i = 0; i < hv.Length; i++) hv[i] *= mul;
    }

    /// For tagged (cast-spawned) particles, force the fresh-roll damage path. Particles like the
    /// column never cache p.hVals and Particle.Init does not reset hasHVals, so a stale hasHVals
    /// could make CalculateAndDealDamage copy a cached p.hVals over the values we scale in
    /// PopulateHVals_Post. Clearing hasHVals here makes it use the freshly populated HitManager.hVals
    /// (which we then multiply), so the multiplier reliably applies and never compounds.
    [HarmonyPatch(typeof(HitManager), nameof(HitManager.CalculateAndDealDamage))]
    [HarmonyPrefix]
    // ReSharper disable once InconsistentNaming
    private static void CalculateAndDealDamage_Pre(Particle p)
    {
        if (p != null && _taggedParticles.ContainsKey(p))
            p.hasHVals = false;
    }
}
