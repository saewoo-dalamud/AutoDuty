using AutoDuty.Managers;
using AutoDuty.Services.Gearsetter;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.Logging;

namespace AutoDuty.Helpers;

internal sealed class GearsetterGearsetUpdateHelper : ActiveHelperBase<GearsetterGearsetUpdateHelper>
{
    private readonly GearsetterGearsetUpdateService gearsetUpdate = new();

    protected override string Name => nameof(GearsetterGearsetUpdateHelper);
    protected override string DisplayName => "Updating Gearsets";
    protected override int TimeOut { get; set; } = 600_000;

    internal override void Start()
    {
        if (State == ActionState.Running)
        {
            this.DebugLog(this.Name + " already running");
            return;
        }

        try
        {
            this.gearsetUpdate.Prepare();
            base.Start();
        }
        catch (System.Exception ex)
        {
            this.ReportFailure(ex.Message);
        }
    }

    internal override void Stop()
    {
        if (State == ActionState.Running && this.gearsetUpdate.IsActive)
        {
            this.gearsetUpdate.CancelAndRestore();
            return;
        }

        this.gearsetUpdate.Reset();
        base.Stop();
    }

    protected override void HelperUpdate(IFramework framework)
    {
        GearsetterGearsetUpdateProgress progress = this.gearsetUpdate.UpdateNext();
        if (progress == GearsetterGearsetUpdateProgress.InProgress)
            return;

        if (progress == GearsetterGearsetUpdateProgress.Failed)
        {
            this.ReportFailure(this.gearsetUpdate.LastError);
        }
        else
        {
            this.InfoLog($"Updated {this.gearsetUpdate.TargetCount} Gearsetter gearset(s) and restored the original gearset");
        }

        this.gearsetUpdate.Reset();
        base.Stop();
    }

    private void ReportFailure(string error)
    {
        string message = Loc.Get("Overlay.GearsetterGearsetUpdateFailed", error);
        Svc.Log.Warning($"[GearsetterGearsetUpdate] {message}");
        DuoLog.Warning(message);
    }
}
