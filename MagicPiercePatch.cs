using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using LootHero.loot;
using ProjectMage.character;
using ProjectMage.hit;
using ProjectMage.particles;
using Bestiary.monsters;
using ProjectMage.particles.particles.runic;
using ProjectMage.player;

namespace SaS2Resalter;

/// <summary>
/// Lets projectiles pass through enemies instead of dying on the first hit, chosen per item.
///
/// Config: BepInEx/config/amione.SaS2Resalter/magic_pierce.json
/// <code>
/// {
///   "player": ["item:firebomb", "item:rune_elem_fire", "type:17"],
///   "enemy": ["type:67", "type:55"]
/// }
/// </code>
/// Keys are the firing side ("player" for characters with a playerIdx, "enemy" for everything else);
/// 
/// entries are:
/// 
/// "item:&lt;loot name&gt;" (player: only that rune/weapon/throwable pierces),
/// "monster:&lt;monster def name&gt;" (enemy: only that monster's projectiles pierce) or
/// "type:&lt;particle id&gt;" (every projectile of that particle type pierces, also used as a fallback).
/// 
/// A bare number is accepted as "type:&lt;id&gt;" so older configs keep working.
/// 
/// Written by the editor on apply.
///
/// Item keys are resolved when the projectile spawns:
///   - runic arts: the rune the player is casting (RunicAttack.Init scope, inherited by the particles that cast spawns, including their sub-projectiles),
///   - thrown items: the item the thrower last used (PlayerEquipment.UseItem),
///   - ranged weapons: the equipped ranged weapon (slot 6, where bows, crossbows, throwing weapons, channeling rods and throwables are equipped).
///
/// Ranged shots (particle 17) can morph into another particle during their own Init (elemental conversions to 55/27, a throwable turning into the lobbed 71).
/// Those children inherit the parent's item key so the per-item selection still matches the projectile that actually flies.
///
/// How the pierce itself works:
///   1. HitManager.CheckHit prefix  -> characters this projectile already hit are made ethereal for the duration of the vanilla call, so the vanilla scan skips them (restored right after, so nothing else sees the change).
///   2. HitManager.ConnectHit postfix -> records each character the projectile connected with.
///   3. HitManager.CheckHit postfix -> when the projectile did hit something, the result is forced to false so the caller ("if (CheckHit(p)) p.exists = false;") keeps it alive.
///      Terrain collision is checked separately by the callers and still stops the projectile.
/// 
/// The hit itself is the vanilla one, so damage, poise, blocking and effects are unchanged.
/// </summary>
[HarmonyPatch]
public static class MagicPiercePatch
{
    private static string ConfigPath =>
        Path.Combine(Paths.ConfigPath, "amione.SaS2Resalter", "magic_pierce.json");

    private static Dictionary<string, HashSet<string>> _config;
    private static long _lastFileTime;
    private static int _lastCheckTick;

    /// How often the config file is re-checked for changes (only re-read when its timestamp changes).
    private const int CheckIntervalMs = 500;

    public static void ReloadConfig()
    {
        _config = null;
        _lastCheckTick = 0;
    }

    private static Dictionary<string, HashSet<string>> Config
    {
        get
        {
            var empty = new Dictionary<string, HashSet<string>>();
            if (_config != null && unchecked(Environment.TickCount - _lastCheckTick) < CheckIntervalMs)
                return _config;
            _lastCheckTick = Environment.TickCount;

            if (!File.Exists(ConfigPath)) return _config ??= empty;
            var mtime = new FileInfo(ConfigPath).LastWriteTime.Ticks;
            if (_config != null && _lastFileTime == mtime) return _config;
            try
            {
                _config = SimpleJson.ParseStringLists(File.ReadAllText(ConfigPath));
                _lastFileTime = mtime;
                Plugin.Instance.Log.LogInfo($"[MagicPierce] Loaded {_config.Count} side(s).");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Log.LogError($"[MagicPierce] Config error: {ex.Message}");
                _config = empty;
            }

            return _config;
        }
    }

    /// Resolved item/type key of each in-flight projectile.
    /// Cleared when the particle is re-initialized (the particle pool reuses the same objects).
    private static readonly Dictionary<Particle, string> Keys = new();

    /// Characters each in-flight projectile already hit.
    private static readonly Dictionary<Particle, HashSet<int>> HitTargets = new();

    /// Safety valve: the particle pool is finite, but never let these tables grow without bound.
    private const int MaxTrackedParticles = 4096;

    private struct CastState
    {
        public int Owner;
        public string Item;
    }

    /// The rune currently being cast (players only), so its projectiles can be tied to the rune item.
    private static int _castOwner = -1;
    private static string _castItem;

    /// Item key of the particle whose Update is running; sub-particles inherit it.
    private static string _updatingKey;

    /// Item key of the particle whose Init is running;
    /// projectiles it converts into during Init (a ranged shot morphing into particle 55/27, a throwable turning into particle 71) inherit it, because those children spawn before the parent's key is stored.
    private static string _initKey;

    /// Item a character last used, for thrown projectiles.
    private static readonly Dictionary<int, string> LastUsedItem = new();
    private static readonly Dictionary<int, int> LastUsedItemTick = new();

    /// How long after using an item its throw is still attributed to it.
    private const int UsedItemWindowMs = 4000;

    private static Character CharacterOf(int idx)
    {
        if (idx < 0 || idx >= CharMgr.character.Length) return null;
        return CharMgr.character[idx];
    }

    private static string SideOf(Character c) => c.playerIdx > -1 ? "player" : "enemy";

    private static string LootName(int lootIdx)
    {
        if (lootIdx < 0 || lootIdx >= LootCatalog.lootDef.Count) return null;
        return LootCatalog.lootDef[lootIdx].name;
    }

    /// The rune (magic item) a player is charging.
    private static string ChargingRuneName(Character c)
    {
        try
        {
            return LootName(c.chargeLootIdx);
        }
        catch
        {
            return null;
        }
    }

    /// GetLoot is internal, so it is called through AccessTools.
    private static readonly System.Reflection.MethodInfo GetLootMethod =
        AccessTools.Method(typeof(PlayerEquipment), "GetLoot", new[] { typeof(int) });

    /// Equipment slot of the ranged weapon (PlayerEquipment.EQUIPMENT_RANGED).
    /// Bows, crossbows, throwing weapons, channeling rods and throwables all live in slot 6; slots 4 and 5 are the melee weapons (GetWeaponSlotIdx).
    private const int RangedSlot = 6;

    /// The ranged weapon a character has equipped.
    private static string EquippedRangedName(Character c)
    {
        if (c.playerIdx < 0 || c.playerIdx >= PlayerMgr.player.Length) return null;
        var player = PlayerMgr.player[c.playerIdx];
        if (player?.equipment == null || GetLootMethod == null) return null;
        try
        {
            var loot = GetLootMethod.Invoke(player.equipment, new object[] { RangedSlot }) as LootDef;
            return loot?.name;
        }
        catch
        {
            return null;
        }
    }

    /// The item a character threw most recently (PlayerEquipment.UseItem).
    private static string LastUsedItemName(Character c)
    {
        return !LastUsedItemTryGet(c.ID, out var name) ? null : name;
    }

    private static bool LastUsedItemTryGet(int charId, out string name)
    {
        name = null;
        if (!LastUsedItem.TryGetValue(charId, out var stored)) return false;
        if (LastUsedItemTick.TryGetValue(charId, out var tick))
        {
            var age = unchecked(Environment.TickCount - tick);
            if (age < 0 || age > UsedItemWindowMs) return false;
        }
        name = stored;
        return true;
    }

    /// Item/type key of a projectile, resolved once at spawn.
    /// Takes the spawn arguments rather than the particle so it can run before Particle.Init has written them onto the (pooled) particle object.
    private static string ResolveKey(int type, int ownerIdx)
    {
        var owner = CharacterOf(ownerIdx);
        if (owner is not { exists: true }) return null;

        // Monsters have no items: their projectiles are attributed to the monster def itself.
        if (owner.playerIdx < 0)
        {
            var idx = owner.monsterIdx;
            if (idx >= 0 && idx < MonsterCatalog.monsterDef.Count)
            {
                var name = MonsterCatalog.monsterDef[idx].name;
                if (!string.IsNullOrEmpty(name)) return "monster:" + name;
            }
            return "type:" + type;
        }

        switch (type)
        {
            // Runic art projectiles: tie them to the rune being cast when there is one.
            case 67: // RunicMagicProjectile
            case 34: // MagicSeeker
            case 31: // MagicBlastBit
            case 33: // MagicSeekerFarm
            case 51: // RunicMagicBit
            case 50: // RunicMagicFarm
            case 30: // MagicBlastFarm
            case 27: // MagicAreaBit
            case 26: // MagicAreaFarm
            case 23: // MagicBombFarm
            case 24: // MagicBomb
            case 75: // RunicMagicOrbFarm
            case 88: // MagicColumnFarm
            case 89: // MagicColumnBit
                if (_castOwner == ownerIdx && _castItem != null) return "item:" + _castItem;
                if (_updatingKey != null) return _updatingKey;
                return "type:" + type;

            // Thrown items (potions, bombs, throwing weapons).
            case 82: // ThrowPotion
            case 52: // Firebomb
            {
                var item = LastUsedItemName(owner);
                return item != null ? "item:" + item : "type:" + type;
            }

            // Ranged weapon shots (bows, crossbows, throwing weapons, channeling rods).
            case 17: // Ranged
            {
                var weapon = EquippedRangedName(owner);
                return weapon != null ? "item:" + weapon : "type:" + type;
            }

            default:
                return "type:" + type;
        }
    }

    /// True when the particle's resolved key is enabled for the side that fired it.
    private static bool IsPiercing(Particle p)
    {
        if (p == null) return false;
        var owner = CharacterOf(p.owner);
        if (owner is not { exists: true }) return false;
        if (!Config.TryGetValue(SideOf(owner), out var enabled)) return false;
        if (!Keys.TryGetValue(p, out var key) || key == null) return false;
        if (enabled.Contains(key)) return true;
        // "type:<id>" also matches when the resolved key is an item: the caller may have enabled
        // the whole particle type for that side.
        return enabled.Contains("type:" + p.type);
    }

    /// Record the item a player used so a throw shortly after can be attributed to it.
    [HarmonyPatch(typeof(PlayerEquipment), "UseItem", typeof(Character), typeof(int), typeof(bool), typeof(bool))]
    [HarmonyPrefix]
    private static void UseItem_Pre(PlayerEquipment __instance, Character character, int itemIdx)
    {
        try
        {
            if (character == null || __instance?.invItem == null) return;
            if (itemIdx < 0 || itemIdx >= __instance.invItem.Count) return;
            var lootIdx = __instance.invItem[itemIdx].lootIdx;
            var name = LootName(lootIdx);
            if (name == null) return;
            LastUsedItem[character.ID] = name;
            LastUsedItemTick[character.ID] = Environment.TickCount;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Log.LogWarning($"[MagicPierce] UseItem hook failed: {ex.Message}");
        }
    }

    /// Open/close the cast scope so projectiles spawned by a runic art are tied to its rune.
    [HarmonyPatch(typeof(RunicAttack), nameof(RunicAttack.Init))]
    [HarmonyPrefix]
    private static void RunicInit_Pre(int owner, out CastState __state)
    {
        __state = new CastState { Owner = _castOwner, Item = _castItem };
        _castOwner = -1;
        _castItem = null;

        var c = CharacterOf(owner);
        if (c is not { exists: true } || c.playerIdx < 0) return; // players only, monsters have no rune item
        var rune = ChargingRuneName(c);
        if (rune == null) return;
        _castOwner = owner;
        _castItem = rune;
    }

    [HarmonyPatch(typeof(RunicAttack), nameof(RunicAttack.Init))]
    [HarmonyPostfix]
    private static void RunicInit_Post(CastState __state)
    {
        _castOwner = __state.Owner;
        _castItem = __state.Item;
    }

    /// Resolve and store the projectile's key before its behavior Init runs.
    /// Init is where ranged weapons morph their shot into another particle (17 -> 55/27/71), so resolving first lets those children inherit the item that was fired.
    [HarmonyPatch(typeof(Particle), nameof(Particle.Init))]
    [HarmonyPrefix]
    private static void ParticleInit_Pre(Particle __instance, int type, int owner, out string __state)
    {
        __state = _initKey;
        try
        {
            if (__instance == null)
            {
                _initKey = null;
                return;
            }

            var key = ResolveKey(type, owner);
            // Conversion children have no item of their own; keep the parent's item key.
            if ((key == null || !key.StartsWith("item:")) && _initKey != null && _initKey.StartsWith("item:"))
                key = _initKey;

            if (key != null) Keys[__instance] = key;
            else Keys.Remove(__instance);
            _initKey = key != null && key.StartsWith("item:") ? key : null;
        }
        catch (Exception ex)
        {
            _initKey = __state;
            Plugin.Instance.Log.LogWarning($"[MagicPierce] Init hook failed: {ex.Message}");
        }
    }

    [HarmonyPatch(typeof(Particle), nameof(Particle.Init))]
    [HarmonyPostfix]
    private static void ParticleInit_Post(Particle __instance, string __state)
    {
        _initKey = __state;
        if (__instance == null) return;
        if (Keys.Count > MaxTrackedParticles || HitTargets.Count > MaxTrackedParticles)
        {
            Keys.Clear();
            HitTargets.Clear();
        }
        HitTargets.Remove(__instance);
    }

    /// Sub-particles (seekers, trails, turret shots) inherit the parent's item key while its Update runs; the magic particle types read it during ResolveKey.
    /// Ranged conversions are handled at Init instead (ParticleInit_Pre).
    [HarmonyPatch(typeof(Particle), nameof(Particle.Update))]
    [HarmonyPrefix]
    private static void ParticleUpdate_Pre(Particle __instance, out string __state)
    {
        __state = _updatingKey;
        _updatingKey = null;
        if (__instance == null || !Keys.TryGetValue(__instance, out var key)) return;
        if (key != null && key.StartsWith("item:")) _updatingKey = key;
    }

    [HarmonyPatch(typeof(Particle), nameof(Particle.Update))]
    [HarmonyPostfix]
    private static void ParticleUpdate_Post(string __state)
    {
        _updatingKey = __state;
    }

    /// Hide already-hit targets from the vanilla scan by flipping ethereal for the call's duration.
    [HarmonyPatch(typeof(HitManager), nameof(HitManager.CheckHit))]
    [HarmonyPrefix]
    private static void CheckHit_Pre(Particle p, out List<Character> __state)
    {
        __state = null;
        if (!IsPiercing(p)) return;
        if (!HitTargets.TryGetValue(p, out var ids) || ids.Count == 0) return;

        List<Character> hidden = null;
        foreach (var c in ids.Select(CharacterOf).Where(c => c is { exists: true } && !c.ethereal))
        {
            hidden ??= [];
            c.ethereal = true;
            hidden.Add(c);
        }

        __state = hidden;
    }

    /// Record a connected hit so the projectile never hits the same character twice.
    [HarmonyPatch(typeof(HitManager), "ConnectHit")]
    [HarmonyPostfix]
    private static void ConnectHit_Post(Character targ, Particle p)
    {
        if (targ == null || p == null) return;
        if (!IsPiercing(p)) return;

        if (!HitTargets.TryGetValue(p, out var ids))
        {
            ids = [];
            HitTargets[p] = ids;
        }
        ids.Add(targ.ID);
    }

    /// Restore the hidden characters and keep the projectile alive after a connected hit.
    [HarmonyPatch(typeof(HitManager), nameof(HitManager.CheckHit))]
    [HarmonyPostfix]
    private static void CheckHit_Post(Particle p, ref bool __result, List<Character> __state)
    {
        if (__state != null)
        {
            // A character that died from this hit keeps ethereal (DoDeath set it); only the characters we hid ourselves are restored.
            foreach (var c in __state.Where(c => c is { dyingFrame: <= 0f }))
            {
                c.ethereal = false;
            }
        }

        if (!__result) return;
        if (!IsPiercing(p)) return;
        __result = false;
    }
}
