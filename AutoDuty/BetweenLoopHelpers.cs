using AutoDuty.Helpers;
namespace AutoDuty;

using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

// Extracted from AutoDuty.LoopTasks()'s second `if (between)` block + AutoEquipRecommendedGear().
// If a merge conflict lands on the old spot in AutoDuty.cs, the logic now lives here.
internal sealed class BetweenLoopHelpers(TaskManager taskManager)
{

    internal unsafe void Run()
    {
        // 정제 (마테리아 정제 퀘스트 완료 시)
        if (Configuration.AutoExtract && QuestManager.IsQuestComplete(66174))
            this.EnqueueActiveHelper<ExtractHelper>();
        
        // 장비갱신
        this.AutoEquipRecommendedGear();
        
        // 수리
        if (Configuration.AutoRepair && InventoryHelper.CanRepair())
            this.EnqueueActiveHelper<RepairHelper>();

        // 환상의 옷장에 보관
        if (Configuration.GlamourChestEntrust)
            this.EnqueueActiveHelper<GlamourChestHelper>();

        // 추억의 보관함에 보관
        if (Configuration.ArmoireEntrust)
            this.EnqueueActiveHelper<ArmoireHelper>();

        // 분해
        if (Configuration.AutoDesynth)
            this.EnqueueActiveHelper<DesynthHelper>();

        // 부대 납품 (인벤토리 여유 슬롯 조건 충족 시)
        if (Configuration.AutoGCTurnin && (!Configuration.AutoGCTurninSlotsLeftBool || InventoryManager.Instance()->GetEmptySlotsInBag() <= Configuration.AutoGCTurninSlotsLeft) && PlayerHelper.GetGrandCompanyRank() > 5)
            this.EnqueueActiveHelper<GCTurninHelper>();

        // 트리플 트라이어드 카드 등록
        if (Configuration.TripleTriadRegister)
            this.EnqueueActiveHelper<TripleTriadCardUseHelper>();
        
        // 트리플 트라이어드 카드 판매
        if (Configuration.TripleTriadSell)
            this.EnqueueActiveHelper<TripleTriadCardSellHelper>();

        // 아이템 버리기
        if (Configuration.DiscardItems)
            this.EnqueueActiveHelper<DiscardHelper>();

        // 귀환 처리
        if (Configuration.DutyModeEnum != DutyMode.Squadron && Configuration.RetireMode)
        {
            taskManager.Enqueue(() => Svc.Log.Debug($"Retire Between Loop Action"));

            switch (Configuration.RetireLocationEnum)
            {
                case RetireLocation.GC_Barracks:
                    taskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Loop-GotoBarracksInvoke");
                    break;
                case RetireLocation.Inn:
                    taskManager.Enqueue(() => GotoInnHelper.Invoke(), "Loop-GotoInnInvoke");
                    break;
                case RetireLocation.Apartment:
                case RetireLocation.Personal_Home:
                case RetireLocation.FC_Estate:
                default:
                    Svc.Log.Info($"{(Housing)Configuration.RetireLocationEnum} {Configuration.RetireLocationEnum}");
                    taskManager.Enqueue(() => GotoHousingHelper.Invoke((Housing)Configuration.RetireLocationEnum), "Loop-GotoHousingInvoke");
                    break;
            }

            taskManager.EnqueueDelay(50);
            taskManager.Enqueue(() => GotoHousingHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, "Loop-WaitGotoComplete",
                                     new TaskManagerConfiguration(int.MaxValue));
        }
    }

    // Gearsetter 추천 장비로 자동 교체
    internal void AutoEquipRecommendedGear()
    {
        if (!Configuration.AutoEquipRecommendedGear)
            return;

        taskManager.Enqueue(() => Svc.Log.Debug($"AutoEquipRecommendedGear Between Loop Action"));
        taskManager.Enqueue(() => AutoEquipHelper.Invoke(), "AutoEquipRecommendedGear-Invoke");
        taskManager.EnqueueDelay(50);
        taskManager.Enqueue(() => AutoEquipHelper.State != ActionState.Running, "AutoEquipRecommendedGear-WaitAutoEquipComplete", new TaskManagerConfiguration(int.MaxValue));
        taskManager.Enqueue(() => PlayerHelper.IsReadyFull, "AutoEquipRecommendedGear-WaitANotIsOccupied");
    }

    // 주어진 ActiveHelper를 실행하고 완료될 때까지 대기하는 태스크를 큐에 등록
    private void EnqueueActiveHelper<T>() where T : ActiveHelperBase<T>, new()
    {
        taskManager.Enqueue(() => Svc.Log.Debug($"Enqueueing {typeof(T).Name}"), "Loop-ActiveHelper");
        taskManager.Enqueue(() => ActiveHelperBase<T>.Invoke(), $"Loop-{typeof(T).Name}");
        taskManager.EnqueueDelay(50);
        taskManager.Enqueue(() => ActiveHelperBase<T>.State != ActionState.Running, $"Loop-Wait-{typeof(T).Name}-Complete", new TaskManagerConfiguration(int.MaxValue));
        taskManager.Enqueue(() => PlayerHelper.IsReadyFull, "Loop-WaitIsReadyFull");
    }
}
