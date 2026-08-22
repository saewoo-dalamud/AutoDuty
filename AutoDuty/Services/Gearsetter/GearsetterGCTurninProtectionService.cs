using AutoDuty.Helpers;
using AutoDuty.IPC;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace AutoDuty.Services.Gearsetter;

using System;
using System.Collections.Generic;
using System.Linq;

internal enum GearsetterProtectionProgress
{
    InProgress,
    Complete,
    Failed,
}

/// <summary>
/// Moves Gearsetter-recommended upgrades out of the player bags and into their
/// matching Armoury Chest containers before a GC delivery begins. Items stay in
/// the Armoury Chest, where AutoRetainer's Hide Armoury Chest Items mode ignores them.
/// </summary>
internal sealed unsafe class GearsetterGCTurninProtectionService
{
    private const int MoveTimeoutMilliseconds = 5_000;

    private readonly List<PlannedMove> plannedMoves = [];
    private int moveIndex;
    private PendingMove? pendingMove;

    internal string LastError { get; private set; } = string.Empty;
    internal int PlannedMoveCount => this.plannedMoves.Count;

    internal bool Prepare(out string error)
    {
        this.Reset();

        List<GearsetterRecommendation> recommendations = GearsetterRecommendationService.CollectRecommendations()
            .Where(x => GearsetterRecommendationService.IsMainInventory(x.InventoryType))
            .Where(x => !AutoRetainer_IPCSubscriber.IsItemProtected(x.ItemId))
            .DistinctBy(x => (x.InventoryType, x.InventorySlot))
            .ToList();

        Dictionary<InventoryType, Queue<ushort>> freeSlots = [];
        foreach (IGrouping<InventoryType, GearsetterRecommendation> group in recommendations.GroupBy(x => GetArmouryInventory(x.TargetSlot)))
        {
            if (group.Key == InventoryType.Invalid)
            {
                error = $"Could not determine an Armoury Chest container for {group.First().TargetSlot}.";
                return false;
            }

            Queue<ushort> available = GetEmptySlots(group.Key);
            if (available.Count < group.Count())
            {
                error = $"The {group.Key} container needs {group.Count()} free slots, but only {available.Count} are available.";
                return false;
            }

            freeSlots[group.Key] = available;
        }

        foreach (GearsetterRecommendation recommendation in recommendations)
        {
            InventoryItem* source = InventoryManager.Instance()->GetInventorySlot(recommendation.InventoryType, recommendation.InventorySlot);
            if (source == null || source->ItemId != recommendation.ItemId)
            {
                error = $"Gearsetter recommendation {recommendation.ItemId} is no longer in {recommendation.InventoryType} slot {recommendation.InventorySlot}.";
                return false;
            }

            InventoryType armouryInventory = GetArmouryInventory(recommendation.TargetSlot);
            this.plannedMoves.Add(new PlannedMove(
                recommendation.ItemId,
                recommendation.InventoryType,
                (ushort)recommendation.InventorySlot,
                armouryInventory,
                freeSlots[armouryInventory].Dequeue()));
        }

        error = string.Empty;
        return true;
    }

    internal GearsetterProtectionProgress MoveNext()
    {
        if (this.pendingMove is { } pending)
        {
            if (IsExpectedItem(pending.ToInventory, pending.ToSlot, pending.ItemId))
            {
                this.pendingMove = null;
                this.moveIndex++;
                return this.moveIndex >= this.plannedMoves.Count
                    ? GearsetterProtectionProgress.Complete
                    : GearsetterProtectionProgress.InProgress;
            }

            if (Environment.TickCount64 <= pending.Deadline)
                return GearsetterProtectionProgress.InProgress;

            return this.Fail($"Timed out moving item {pending.ItemId} into the Armoury Chest.");
        }

        if (this.moveIndex >= this.plannedMoves.Count)
            return GearsetterProtectionProgress.Complete;

        PlannedMove move = this.plannedMoves[this.moveIndex];
        if (!IsExpectedItem(move.SourceInventory, move.SourceSlot, move.ItemId))
            return this.Fail($"Item {move.ItemId} is no longer in its expected inventory slot.");
        if (!IsEmpty(move.ArmouryInventory, move.ArmourySlot))
            return this.Fail($"The reserved {move.ArmouryInventory} slot is no longer empty.");

        InventoryManager.Instance()->MoveItemSlot(move.SourceInventory, move.SourceSlot, move.ArmouryInventory, move.ArmourySlot, true);
        this.pendingMove = new PendingMove(
            move.ItemId,
            move.ArmouryInventory,
            move.ArmourySlot,
            Environment.TickCount64 + MoveTimeoutMilliseconds);
        return GearsetterProtectionProgress.InProgress;
    }

    internal void Reset()
    {
        this.plannedMoves.Clear();
        this.moveIndex = 0;
        this.pendingMove = null;
        this.LastError = string.Empty;
    }

    private GearsetterProtectionProgress Fail(string error)
    {
        this.LastError = error;
        return GearsetterProtectionProgress.Failed;
    }

    private static InventoryType GetArmouryInventory(RaptureGearsetModule.GearsetItemIndex targetSlot) => targetSlot switch
    {
        RaptureGearsetModule.GearsetItemIndex.MainHand => InventoryType.ArmoryMainHand,
        RaptureGearsetModule.GearsetItemIndex.OffHand => InventoryType.ArmoryOffHand,
        RaptureGearsetModule.GearsetItemIndex.Head => InventoryType.ArmoryHead,
        RaptureGearsetModule.GearsetItemIndex.Body => InventoryType.ArmoryBody,
        RaptureGearsetModule.GearsetItemIndex.Hands => InventoryType.ArmoryHands,
        RaptureGearsetModule.GearsetItemIndex.Legs => InventoryType.ArmoryLegs,
        RaptureGearsetModule.GearsetItemIndex.Feet => InventoryType.ArmoryFeets,
        RaptureGearsetModule.GearsetItemIndex.Ears => InventoryType.ArmoryEar,
        RaptureGearsetModule.GearsetItemIndex.Neck => InventoryType.ArmoryNeck,
        RaptureGearsetModule.GearsetItemIndex.Wrists => InventoryType.ArmoryWrist,
        RaptureGearsetModule.GearsetItemIndex.RingLeft or RaptureGearsetModule.GearsetItemIndex.RingRight => InventoryType.ArmoryRings,
        _ => InventoryType.Invalid,
    };

    private static Queue<ushort> GetEmptySlots(InventoryType inventoryType)
    {
        Queue<ushort> result = [];
        InventoryContainer* container = InventoryManager.Instance()->GetInventoryContainer(inventoryType);
        if (container == null)
            return result;

        for (ushort slot = 0; slot < container->Size; slot++)
        {
            if (container->Items[slot].ItemId == 0)
                result.Enqueue(slot);
        }

        return result;
    }

    private static bool IsExpectedItem(InventoryType inventoryType, ushort slot, uint itemId)
    {
        InventoryItem* item = InventoryManager.Instance()->GetInventorySlot(inventoryType, slot);
        return item != null && item->ItemId == itemId;
    }

    private static bool IsEmpty(InventoryType inventoryType, ushort slot)
    {
        InventoryItem* item = InventoryManager.Instance()->GetInventorySlot(inventoryType, slot);
        return item != null && item->ItemId == 0;
    }

    private readonly record struct PlannedMove(
        uint ItemId,
        InventoryType SourceInventory,
        ushort SourceSlot,
        InventoryType ArmouryInventory,
        ushort ArmourySlot);

    private readonly record struct PendingMove(
        uint ItemId,
        InventoryType ToInventory,
        ushort ToSlot,
        long Deadline);
}
