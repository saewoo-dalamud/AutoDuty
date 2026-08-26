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
    // RaptureGearsetModule stores up to 100 gearset entries (FixedSizeArray100<GearsetEntry>). Entry IDs
    // are assigned at creation time and never get compacted/reused when a gearset is deleted, so this is
    // the only safe loop bound - RaptureGearsetModule.NumGearsets is a count of currently-existing
    // gearsets, not the highest entry ID in use.
    private const int MaxGearsetEntries = 100;

    internal static IReadOnlyList<GearsetterRecommendation> CollectRecommendations()
    {
        if (!Gearsetter_IPCSubscriber.IsEnabled)
            return [];

        RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
            return [];

        List<GearsetterRecommendation> recommendations = [];
        HashSet<(int GearsetId, InventoryType InventoryType, int InventorySlot)> seenRecommendations = [];

        // NumGearsets is a COUNT of existing gearsets, not "highest entry ID + 1" - entry IDs don't get
        // compacted when a gearset is deleted, so a later-created gearset can sit at a raw index well past
        // NumGearsets (e.g. gearset #35 at entry ID 34 when only 20 gearsets currently exist). Looping to
        // NumGearsets silently never reaches it. Scan the full fixed capacity instead and let
        // IsValidGearset/GearsetFlag.Exists do the actual filtering.
        for (int gearsetId = 0; gearsetId < MaxGearsetEntries; gearsetId++)
        {
            if (!gearsetModule->IsValidGearset(gearsetId))
                continue;

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

    // 모든(존재하는) 기어셋이 참조하는 아이템ID 전체를 모음. FFXIV 기어셋은 슬롯 위치가 아니라
    // 아이템ID로 저장되므로, 어떤 아이템이 인벤토리 어디에 있든 여기 포함되면 "어느 기어셋이든
    // 이 아이템을 필요로 한다"는 뜻임.
    //
    // excludeGearsetId: 지금 갱신 중인 기어셋은 제외해야 함 - RaptureGearsetModule.UpdateGearset()으로
    // 저장하기 전까지는 그 기어셋의 저장된 정의가 여전히 "방금 갈아입어서 빼낸 옛 아이템"을
    // 가리키고 있어서, 제외하지 않으면 그 옛 아이템이 "지금 자기 자신이 여전히 쓰는 아이템"으로
    // 잘못 판정되어 가방으로 절대 옮겨지지 않는 버그가 있었음.
    internal static HashSet<uint> CollectAllGearsetItemIds(int? excludeGearsetId = null)
    {
        HashSet<uint> itemIds = [];

        RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
        if (gearsetModule == null)
            return itemIds;

        // NumGearsets is a count, not a max index - see the comment in CollectRecommendations().
        for (int gearsetId = 0; gearsetId < MaxGearsetEntries; gearsetId++)
        {
            if (gearsetId == excludeGearsetId || !gearsetModule->IsValidGearset(gearsetId))
                continue;

            RaptureGearsetModule.GearsetEntry* gearset = gearsetModule->GetGearset(gearsetId);
            if (gearset == null || !gearset->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                continue;

            foreach (RaptureGearsetModule.GearsetItem item in gearset->Items)
                if (item.ItemId > 0)
                    itemIds.Add(item.ItemId);
        }

        return itemIds;
    }
}
