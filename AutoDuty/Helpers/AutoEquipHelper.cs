using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using AutoDuty.IPC;
using AutoDuty.Services.Gearsetter;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Throttlers;

namespace AutoDuty.Helpers
{
    using System;
    using System.Collections.Generic;
    using Lumina.Excel.Sheets;

    internal unsafe class AutoEquipHelper : ActiveHelperBase<AutoEquipHelper>
    {
        public override string[]? Commands { get; init; } = ["autoequip", "equiprec"];
        public override string? CommandDescription { get; init; } = "Equips recommended gear";

        private GearsetUpdateSource? requestedSource;
        private bool suppressPortraitUpdate;
        // 각 소스의 흐름이 의도한 장비를 전부 다 갈아입은 뒤에만 true가 됨.
        // Stop()은 장비를 다 갈아입기 전에도(헬퍼 자체의 TimeOut이나 외부 ForceStop 등으로) 호출될
        // 수 있는데, 그 시점에 기어셋을 저장하면 그 순간 우연히 입고 있던 불완전한 장비 구성으로
        // 기어셋의 기존 저장 내용을 덮어써버림.
        private bool completedSuccessfully;

        internal static void Invoke(GearsetUpdateSource source, bool suppressPortraitUpdate = false)
        {
            Instance.requestedSource = source;
            Instance.suppressPortraitUpdate = suppressPortraitUpdate;
            Instance.Start();
        }

        internal override void Start()
        {
            switch (this.requestedSource ?? Configuration.AutoEquipRecommendedGearSource)
            {
                case GearsetUpdateSource.Gearsetter when Gearsetter_IPCSubscriber.IsEnabled:
                    this.TimeOut = 10_000;
                    this.source  = GearsetUpdateSource.Gearsetter;
                    break;
                case GearsetUpdateSource.Stylist when Stylist_IPCSubscriber.IsEnabled:
                    this.TimeOut = 10_000;
                    this.source  = GearsetUpdateSource.Stylist;
                    break;
                default:
                    this.TimeOut = 5_000;
                    this.source  = GearsetUpdateSource.Vanilla;
                    break;
            }
            base.Start();
        }

        private GearsetUpdateSource source;

        protected override string Name        => nameof(AutoEquipHelper);
        protected override string DisplayName => "Auto Equip";

        protected override int TimeOut { get; set; }


        protected override void     HelperUpdate(IFramework framework)
        {
            switch (this.source)
            {
                case GearsetUpdateSource.Vanilla:
                    this.AutoEquipUpdate(framework);
                    break;
                case GearsetUpdateSource.Gearsetter:
                    this.AutoEquipGearSetterUpdate(framework);
                    break;
                case GearsetUpdateSource.Stylist:
                    this.AutoEquipStylistUpdate(framework);
                    break;
            }
        }

        internal override void Stop()
        {
            base.Stop();

            if (this.completedSuccessfully)
                RaptureGearsetModule.Instance()->UpdateGearset(RaptureGearsetModule.Instance()->CurrentGearsetIndex);
            else
                this.DebugLog("Stopped before equipping finished - not saving the gearset to avoid overwriting it with a partial loadout");

            this.completedSuccessfully = false;
            this._statesExecuted = AutoEquipState.None;
            this._gearset        = null;
            this._pendingEquips  = null;
            this._equipTicksWithoutProgress = 0;
            this.requestedSource = null;
            bool updatePortrait = !this.suppressPortraitUpdate;
            this.suppressPortraitUpdate = false;
            if (updatePortrait)
                PortraitHelper.Invoke();
        }

        [Flags]
        enum AutoEquipState : int
        {
            None                                  = 0,
            Setting_Up                            = 1 << 0,
            Equipping                             = 1 << 1,
            Updating_Gearset                      = 1 << 2,
            Getting_Recommended_Gear              = 1 << 3,
            Recommended_Gear_Need_Second_Pass     = 1 << 4,
            Updating_Gearset_Second_Pass          = 1 << 5,
            Getting_Recommended_Gear_Second_Pass  = 1 << 6,
            MovingOldGear                         = 1 << 7,
        }

        private AutoEquipState _statesExecuted = AutoEquipState.None;

        private void AutoEquipUpdate(IFramework framework)
        {
            if (!EzThrottler.Throttle(this.Name, 250))
                return;

            if (RecommendEquipModule.Instance()->IsUpdating)
                    return;

            if (!this._statesExecuted.HasFlag(AutoEquipState.Setting_Up))
            {
                this.DebugLog($"RecommendEquipModule - SetupForClassJob");
                RecommendEquipModule.Instance()->SetupForClassJob((byte)Player.ClassJob.RowId);
                this._statesExecuted |= AutoEquipState.Setting_Up;
            }
            else if (!this._statesExecuted.HasFlag(AutoEquipState.Equipping))
            {
                this.DebugLog($"RecommendEquipModule - EquipRecommendedGear");
                RecommendEquipModule.Instance()->EquipRecommendedGear();
                this._statesExecuted |= AutoEquipState.Equipping;
            }
            else
            {
                this.DebugLog($"Stop");
                this.completedSuccessfully = true;
                this.Stop();
            }
        }

        private List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)>? _gearset = null;
        // 아직 장착 확인이 안 된 항목들. 한 틱에 MoveItemSlot을 여러 번 연달아 호출하면 첫 번째만
        // 실제로 반영되고 나머지는 씹히는 것으로 보여서(검증해보니 그랬음), 매 틱 장착 시도 후
        // 실제로 반영됐는지 확인하고, 안 된 것만 남겨서 다음 틱에 다시 시도함.
        private List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)>? _pendingEquips = null;
        private const int MaxEquipTicksWithoutProgress = 3;
        private int _equipTicksWithoutProgress = 0;

        private void AutoEquipGearSetterUpdate(IFramework framework)
        {
            if (!EzThrottler.Check("AutoEquipGearSetter"))
                return;

            EzThrottler.Throttle("AutoEquipGearSetter", 50);

            if (!this._statesExecuted.HasFlag(AutoEquipState.Updating_Gearset))
            {
                this.DebugLog($"RaptureGearsetModule - UpdateGearset");
                RaptureGearsetModule.Instance()->UpdateGearset(RaptureGearsetModule.Instance()->CurrentGearsetIndex);
                this._statesExecuted |= AutoEquipState.Updating_Gearset;
                EzThrottler.Throttle("AutoEquipGearSetter", 500, true);
            }
            else if (!this._statesExecuted.HasFlag(AutoEquipState.Getting_Recommended_Gear))
            {
                this.DebugLog($"Gearsetter_IPCSubscriber - GetRecommendationsForGearset");
                this._gearset     =  Gearsetter_IPCSubscriber.GetRecommendationsForGearset((byte)RaptureGearsetModule.Instance()->CurrentGearsetIndex);
                this._pendingEquips = this._gearset == null ? null : [..this._gearset];
                this._equipTicksWithoutProgress = 0;
                this._statesExecuted |= AutoEquipState.Getting_Recommended_Gear;
            }
            else if (this._pendingEquips != null && this._pendingEquips.Count > 0)
            {
                // 추천 목록의 새 장비를 장착. MoveItemSlot의 a6=true 스왑 기능 덕분에 목적지 슬롯이
                // 이미 차있어도 미리 벗길 필요 없이 그냥 장착하면 됨 - 기존에 껴있던 장비는 자동으로
                // 새 장비가 있던 출처 슬롯으로 스왑되어 들어감.
                //
                // 한 틱 안에서 MoveItemSlot을 여러 번 연달아 호출하면 첫 번째만 실제로 반영되고
                // 나머지는 씹히는 것으로 확인돼서, 매 틱 시도 후 실제로 장착됐는지 검증하고 안 된
                // 것만 다음 틱에 다시 시도함. 반지처럼 같은 아이템을 두 슬롯에 추천받은 경우처럼
                // 소스 자체가 어긋난 항목은 EquipGearsetTick이 별도로 표시해서 2차 패스로 넘김.
                this.DebugLog($"AutoEquipHelper - Equipping recommended gear ({this._pendingEquips.Count} remaining)");
                int before = this._pendingEquips.Count;
                (this._pendingEquips, bool needsSecondPass) = EquipGearsetTick(this._pendingEquips);

                if (needsSecondPass)
                    this._statesExecuted |= AutoEquipState.Recommended_Gear_Need_Second_Pass;

                if (this._pendingEquips.Count == before)
                {
                    this._equipTicksWithoutProgress++;
                    if (this._equipTicksWithoutProgress >= MaxEquipTicksWithoutProgress)
                    {
                        Svc.Log.Warning($"AutoEquipHelper: no progress equipping for {MaxEquipTicksWithoutProgress} ticks, giving up on {this._pendingEquips.Count} remaining item(s)");
                        this._pendingEquips = [];
                    }
                }
                else
                {
                    this._equipTicksWithoutProgress = 0;
                }
            }
            else if (this._gearset != null && !this._statesExecuted.HasFlag(AutoEquipState.Equipping))
            {
                this._statesExecuted |= AutoEquipState.Equipping;
            }
            else if (this._gearset != null && !this._statesExecuted.HasFlag(AutoEquipState.MovingOldGear))
            {
                // 2틱: 옵션이 켜져 있으면, 방금 스왑으로 밀려난 옛 장비들을 가방으로 옮김. 장착이
                // 이미 끝난 뒤에 하는 순수 정리 작업이라, 이 단계가 실패해도(가방 꽉 참 등) 캐릭터는
                // 이미 올바르게 장착된 상태임.
                this.DebugLog("AutoEquipHelper - Moving swapped-out gear to inventory");
                MoveSwappedOutGearToInventory(this._gearset);
                this._statesExecuted |= AutoEquipState.MovingOldGear;
            }
            else if (this._statesExecuted.HasFlag(AutoEquipState.Recommended_Gear_Need_Second_Pass) && !this._statesExecuted.HasFlag(AutoEquipState.Updating_Gearset_Second_Pass))
            {
                this.DebugLog($"RaptureGearsetModule - UpdateGearsetSecondPass");
                RaptureGearsetModule.Instance()->UpdateGearset(RaptureGearsetModule.Instance()->CurrentGearsetIndex);
                this._statesExecuted |= AutoEquipState.Updating_Gearset_Second_Pass;
                EzThrottler.Throttle("AutoEquipGearSetter", 500, true);
            }
            else if (this._statesExecuted.HasFlag(AutoEquipState.Recommended_Gear_Need_Second_Pass) && !this._statesExecuted.HasFlag(AutoEquipState.Getting_Recommended_Gear_Second_Pass))
            {
                this.DebugLog($"Gearsetter_IPCSubscriber - GetRecommendationsForGearset (second pass)");
                this._gearset     =  Gearsetter_IPCSubscriber.GetRecommendationsForGearset((byte)RaptureGearsetModule.Instance()->CurrentGearsetIndex);
                this._pendingEquips = this._gearset == null ? null : [..this._gearset];
                this._equipTicksWithoutProgress = 0;
                this._statesExecuted |= AutoEquipState.Getting_Recommended_Gear_Second_Pass;
                // 새로 받아온 목록으로 장착/정리를 다시 실행.
                this._statesExecuted &= ~(AutoEquipState.Equipping | AutoEquipState.MovingOldGear);
            }
            else
            {
                this.DebugLog($"Gearsetter doesn't recommend any more");
                this.completedSuccessfully = true;
                this.Stop();
            }
        }

        // 옵션이 켜져 있으면, EquipGearset이 스왑해서 밀어낸 옛 장비들을 가방으로 옮김. 새 장비
        // 장착은 이미 네이티브 스왑으로 원자적으로 끝난 뒤라(주무기가 빈 채로 남는 순간이 없음),
        // 무기도 방어구/장신구와 동일하게 정리 대상에 포함함. 스왑 직후엔 옛 장비가 항상 그
        // 아이템의 원래 출처 슬롯에 가 있으므로, 그 자리에 지금 뭐가 있는지만 확인하면 됨.
        //
        // 단, 그 옛 장비가 다른(존재하는) 기어셋에서도 여전히 참조되는 아이템이면 가방으로
        // 옮기지 않고 장비함(스왑된 자리)에 그대로 둠 - 가방만 대상으로 하는 외부 자동판매/자동분해류
        // 플러그인/매크로에 노출되는 걸 줄이기 위함. AutoDuty 코드 안에서는 이미 아이템ID로
        // 찾으니 위치가 장비함이든 가방이든 상관없지만, 외부 도구는 가방만 훑는 경우가 많음.
        private static unsafe void MoveSwappedOutGearToInventory(List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> gearset)
        {
            if (!Configuration.AutoEquipRecommendedGearGearsetterOldToInventory)
                return;

            // 지금 갱신 중인(=옛 장비를 방금 빼낸) 기어셋 자신은 제외 - UpdateGearset()으로 저장하기
            // 전까지는 그 기어셋의 저장된 정의가 여전히 옛 아이템을 가리키고 있어서, 제외 안 하면
            // 옛 장비가 "자기 자신이 여전히 쓰는 아이템"으로 잘못 판정되어 절대 이동되지 않음.
            HashSet<uint> itemsUsedByGearsets = GearsetterRecommendationService.CollectAllGearsetItemIds(
                excludeGearsetId: RaptureGearsetModule.Instance()->CurrentGearsetIndex);

            foreach ((uint itemId, InventoryType? sourceInventory, byte? sourceSlot, RaptureGearsetModule.GearsetItemIndex _) in gearset)
            {
                if (sourceInventory == null || sourceSlot == null)
                    continue;

                InventoryItem swappedOut = InventoryManager.Instance()->GetInventoryContainer(sourceInventory.Value)->Items[(int)sourceSlot];
                if (swappedOut.IsEmpty() || swappedOut.GetItemId() == itemId)
                    continue; // 스왑이 안 일어났거나(원래 그 슬롯이 비어있었음) 장착 자체가 안 됐음 - HQ 여부와
                              // 무관하게 비교하려면 GetItemId()로 오프셋을 벗겨내야 함

                if (itemsUsedByGearsets.Contains(swappedOut.GetItemId()))
                {
                    Svc.Log.Debug($"AutoEquipHelper: leaving swapped-out item {swappedOut.ItemId} in place - still referenced by another gearset");
                    continue;
                }

                if (InventoryManager.Instance()->GetEmptySlotsInBag() < 1)
                {
                    Svc.Log.Debug("AutoEquipHelper: skipping move-to-inventory, no empty inventory slot");
                    continue;
                }

                (InventoryType inv, ushort slot) = InventoryHelper.GetFirstAvailableSlot(InventoryHelper.Bag);
                if (slot <= 0)
                {
                    Svc.Log.Debug("AutoEquipHelper: skipping move-to-inventory, no empty inventory slot found.. somehow");
                    continue;
                }

                InventoryManager.Instance()->MoveItemSlot(sourceInventory.Value, (ushort)sourceSlot, inv, slot, true);
            }
        }

        // 남은 항목들의 장착을 한 번씩 시도하고, 실제로 장착됐는지 검증함 (같은 틱 안에서
        // MoveItemSlot을 여러 번 연달아 호출하면 첫 번째만 반영되고 나머지는 씹히는 것으로
        // 확인돼서, 검증 없이 "다 됐다"고 가정하면 안 됨). 예상했던 소스 슬롯에 그 아이템이
        // 더 이상 없는 경우(같은 아이템을 두 슬롯에 추천받았는데 앞서 이미 다른 슬롯에 써버린
        // 경우 등)는 이번 패스에서는 포기하고 2차 재조회가 필요함을 알림.
        //
        // 반환값: (아직 장착 확인이 안 된 항목들, 2차 패스가 필요한지)
        private static unsafe (List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> Pending, bool NeedsSecondPass)
            EquipGearsetTick(List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> pending)
        {
            bool needsSecondPass = false;
            List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> stillPending = [];

            InventoryContainer* equipped = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);

            foreach ((uint itemId, InventoryType? sourceInventory, byte? sourceSlot, RaptureGearsetModule.GearsetItemIndex targetSlot) in pending)
            {
                if (sourceInventory == null || sourceSlot == null)
                    continue; // 이 슬롯은 애초에 추천 대상이 아님

                if (equipped->Items[(int)targetSlot].GetItemId() == itemId)
                    continue; // 이전 틱에 이미 장착됨

                Item? itemData = InventoryHelper.GetExcelItem(itemId);
                if (itemData == null)
                    continue;

                // .ItemId includes the HQ offset (+1,000,000) but Gearsetter's itemId is the base Excel
                // sheet ID - comparing the raw field here made every HQ item look "missing". GetItemId()
                // strips the HQ offset so this actually matches.
                if (InventoryManager.Instance()->GetInventoryContainer(sourceInventory.Value)->Items[(int)sourceSlot].GetItemId() != itemId)
                {
                    Svc.Log.Debug($"AutoEquipHelper: expected item {itemId} no longer at {sourceInventory}/{sourceSlot}, needs second pass");
                    needsSecondPass = true;
                    continue;
                }

                InventoryHelper.EquipGear(itemData.Value, sourceInventory.Value, (int)sourceSlot, targetSlot);

                if (equipped->Items[(int)targetSlot].GetItemId() != itemId)
                {
                    // 이번 틱에는 반영이 안 됨 - 다음 틱에 다시 시도
                    stillPending.Add((itemId, sourceInventory, sourceSlot, targetSlot));
                }
            }

            return (stillPending, needsSecondPass);
        }

        private void AutoEquipStylistUpdate(IFramework framework)
        {
            const string throttleName = "AutoEquip_Stylist";

            if (!EzThrottler.Throttle(throttleName, 250))
                return;

            switch (this._statesExecuted)
            {
                case AutoEquipState.None:
                    this._statesExecuted = AutoEquipState.Setting_Up;
                    break;
                case AutoEquipState.Setting_Up:
                    this.DebugLog($"RaptureGearsetModule - UpdateGearset");
                    RaptureGearsetModule.Instance()->UpdateGearset(RaptureGearsetModule.Instance()->CurrentGearsetIndex);
                    this._statesExecuted = AutoEquipState.Equipping;
                    EzThrottler.Throttle(throttleName, 500, true);
                    break;
                case AutoEquipState.Equipping:
                    this.DebugLog($"Stylist - UpdateCurrentGearset");
                    Stylist_IPCSubscriber.UpdateCurrentGearsetEx(true, true);
                    this._statesExecuted = AutoEquipState.Updating_Gearset;
                    break;
                case AutoEquipState.Updating_Gearset:
                    if(!Stylist_IPCSubscriber.IsBusy)
                    {
                        this.completedSuccessfully = true;
                        this.Stop();
                    }
                    break;
                case AutoEquipState.Getting_Recommended_Gear:
                case AutoEquipState.Recommended_Gear_Need_Second_Pass:
                case AutoEquipState.Updating_Gearset_Second_Pass:
                case AutoEquipState.Getting_Recommended_Gear_Second_Pass:
                default:
                    this.DebugLog("How.. did we get here");
                    this.Stop();
                    break;
            }
        }
    }
}
