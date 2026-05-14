using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using LootHero.loot;

namespace SaS2Resalter;

/// Intercepts the game's LootCatalog.Read(string path) so that it opens the custom loot.zls directly via a FileStream instead of routing through  Loader.GetReader, which cannot resolve absolute BepInEx config paths.
/// This fires for BOTH the initial game load AND any hot-reload triggered by RuntimeReloadPatch.
[HarmonyPatch]
public static class LoadPatch
{
    /// Replaces LootCatalog.Read(string path) when a custom loot file is present.
    /// Returns false (skip original) when we handled the load ourselves.
    [HarmonyPatch(typeof(LootCatalog), nameof(LootCatalog.Read), typeof(string))]
    [HarmonyPrefix]
    private static bool ReadPrefix()
    {
        var customPath = Plugin.CustomLootPath;
        if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
            return true; // no custom file, let the original run

        try
        {
            ApplyCustomLoot();
            Plugin.Instance.Log.LogInfo("Loot catalog loaded from custom loot.zls.");
            return false; // skip original
        }
        catch (System.Exception ex)
        {
            Plugin.Instance.Log.LogError($"Failed to load custom loot.zls: {ex}");
            return true; // fall back to vanilla on error
        }
    }

    /// Opens the custom file directly and calls the game's BinaryReader overload,  then replicates the post-read index setup that the string overload normally does.
    /// Can also be called by RuntimeReloadPatch for explicit hot-reloads.
    public static void ApplyCustomLoot()
    {
        using var fs = new FileStream(Plugin.CustomLootPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(fs);
        LootCatalog.Read(reader); // calls the BinaryReader overload directly

        // replicate the post-read index setup from LootCatalog.Read(string)
        LootCatalog.totalReplenishTypes = 0;
        LootCatalog.smallClothesArmorIdx = LootCatalog.GetLootIdxOrNegative("smallclothes_armor");
        LootCatalog.smallClothesBootsIdx = LootCatalog.GetLootIdxOrNegative("smallclothes_boots");
        LootCatalog.cloudFeatherIdx = LootCatalog.GetLootIdxOrNegative("revive_feather");
        LootCatalog.unarmedIdx = LootCatalog.GetLootIdxOrNegative("unarmed");

        var attack = new List<int>();
        var defense = new List<int>();
        var utility = new List<int>();

        for (var i = 0; i < LootCatalog.lootDef.Count; i++)
        {
            var def = LootCatalog.lootDef[i];

            switch (def.type)
            {
                // LootCategory.TYPE_CHARM
                case 6:
                    switch (def.subType)
                    {
                        case 3: attack.Add(i); break; // SUBTYPE_ARTIFACT_ATTACK
                        case 4: defense.Add(i); break; // SUBTYPE_ARTIFACT_DEFENSE
                        case 5: utility.Add(i); break; // SUBTYPE_ARTIFACT_UTILITY
                    }

                    break;
                // LootCategory.TYPE_CONSUMABLE, replenishable
                case 3 when def.lootField[0].bData:
                    LootCatalog.totalReplenishTypes++;
                    break;
            }
        }

        LootCatalog.artifactIdx = new int[3][];
        LootCatalog.artifactIdx[0] = attack.ToArray();
        LootCatalog.artifactIdx[1] = defense.ToArray();
        LootCatalog.artifactIdx[2] = utility.ToArray();
    }
}