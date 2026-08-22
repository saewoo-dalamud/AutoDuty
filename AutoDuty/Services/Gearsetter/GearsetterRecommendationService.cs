using AutoDuty.IPC;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace AutoDuty.Services.Gearsetter;

using System;
using System.Collections.Generic;

internal readonly record struct GearsetterRecommendation(
    uint ItemId,
    InventoryType InventoryType,
    int InventorySlot,
    RaptureGearsetModule.GearsetItemIndex TargetSlot,
    int GearsetId);

/// <summary>
/// Reads Gearsetter's recommendations and exposes inventory-oriented views of them.
/// </summary>
internal static unsafe class GearsetterRecommendationService
{
    internal static IReadOnlyList<GearsetterRecommendation> CollectRecommendations()
    {
        if (!Gearsetter_IPCSubscriber.IsEnabled)
            return [];

        RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
            return [];

        List<GearsetterRecommendation> recommendations = [];
        HashSet<(int GearsetId, InventoryType InventoryType, int InventorySlot)> seenRecommendations = [];

        for (int gearsetId = 0; gearsetId < gearsetModule->NumGearsets; gearsetId++)
        {
            RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(gearsetId);
            if (gearset == null || !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)>? gearsetRecommendations =
                Gearsetter_IPCSubscriber.GetRecommendationsForGearset((byte)gearsetId);

            if (gearsetRecommendations == null)
                continue;

            foreach ((uint itemId, InventoryType? sourceInventory, byte? sourceInventorySlot, RaptureGearsetModule.GearsetItemIndex targetSlot) in gearsetRecommendations)
            {
                if (sourceInventory == null || sourceInventorySlot == null)
                    continue;

                var recommendation = new GearsetterRecommendation(itemId, sourceInventory.Value, sourceInventorySlot.Value, targetSlot, gearsetId);
                if (seenRecommendations.Add((recommendation.GearsetId, recommendation.InventoryType, recommendation.InventorySlot)))
                    recommendations.Add(recommendation);
            }
        }

        return recommendations;
    }

    internal static bool IsMainInventory(InventoryType inventoryType) => inventoryType is
        InventoryType.Inventory1 or
        InventoryType.Inventory2 or
        InventoryType.Inventory3 or
        InventoryType.Inventory4;

    internal static HashSet<(InventoryType InventoryType, int Slot)> CollectAutoDesynthProtectedSlots()
    {
        HashSet<(InventoryType InventoryType, int Slot)> protectedSlots = [];
        if (!Configuration.AutoDesynthProtectGearsetterUpgrades || !Gearsetter_IPCSubscriber.IsEnabled)
            return protectedSlots;

        try
        {
            foreach (GearsetterRecommendation recommendation in CollectRecommendations())
                protectedSlots.Add((recommendation.InventoryType, recommendation.InventorySlot));
        }
        catch (Exception ex)
        {
            Svc.Log.Warning($"[AutoDesynth] Gearsetter IPC call failed while collecting upgrade protection: {ex.Message}");
            protectedSlots.Clear();
        }

        return protectedSlots;
    }
}
