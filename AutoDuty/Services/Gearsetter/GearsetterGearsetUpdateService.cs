using AutoDuty.Helpers;
using AutoDuty.IPC;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace AutoDuty.Services.Gearsetter;

using System;
using System.Collections.Generic;
using System.Linq;

internal enum GearsetterGearsetUpdateProgress
{
    InProgress,
    Complete,
    Failed,
}

/// <summary>
/// Temporarily equips gearsets for which Gearsetter has upgrades, runs the
/// existing AutoEquipHelper for each, and finally restores the original gearset.
/// </summary>
internal sealed unsafe class GearsetterGearsetUpdateService
{
    private const int GearsetSwitchTimeoutMilliseconds = 5_000;

    private enum UpdateStage
    {
        None,
        EquipTarget,
        WaitForTarget,
        StartUpdate,
        WaitForUpdate,
        RestoreOriginal,
        WaitForOriginal,
        Finished,
    }

    private readonly List<byte> targetGearsets = [];
    private UpdateStage stage;
    private int targetIndex;
    private byte originalGearset;
    private long deadline;
    private bool failed;

    internal string LastError { get; private set; } = string.Empty;
    internal int TargetCount => this.targetGearsets.Count;
    internal bool IsActive => this.stage is not UpdateStage.None and not UpdateStage.Finished;

    internal void Prepare()
    {
        this.Reset();

        RaptureGearsetModule* module = RaptureGearsetModule.Instance();
        if (module == null || !module->IsValidGearset(module->CurrentGearsetIndex))
            throw new InvalidOperationException("The current gearset is not valid.");

        this.originalGearset = (byte)module->CurrentGearsetIndex;
        this.targetGearsets.AddRange(GearsetterRecommendationService.CollectRecommendations()
            .Select(x => x.GearsetId)
            .Distinct()
            .Where(module->IsValidGearset)
            .OrderBy(x => x == this.originalGearset ? 0 : 1)
            .ThenBy(x => x)
            .Select(x => (byte)x));

        this.stage = this.targetGearsets.Count == 0
            ? UpdateStage.Finished
            : UpdateStage.EquipTarget;
    }

    internal GearsetterGearsetUpdateProgress UpdateNext()
    {
        RaptureGearsetModule* module = RaptureGearsetModule.Instance();
        if (module == null)
        {
            this.failed = true;
            this.LastError = "The gearset module became unavailable.";
            this.stage = UpdateStage.Finished;
            return GearsetterGearsetUpdateProgress.Failed;
        }

        switch (this.stage)
        {
            case UpdateStage.None:
            case UpdateStage.Finished:
                return this.failed ? GearsetterGearsetUpdateProgress.Failed : GearsetterGearsetUpdateProgress.Complete;

            case UpdateStage.EquipTarget:
            {
                byte target = this.targetGearsets[this.targetIndex];
                if (module->CurrentGearsetIndex == target)
                {
                    this.stage = UpdateStage.StartUpdate;
                    return GearsetterGearsetUpdateProgress.InProgress;
                }

                module->EquipGearset(target);
                this.deadline = Environment.TickCount64 + GearsetSwitchTimeoutMilliseconds;
                this.stage = UpdateStage.WaitForTarget;
                return GearsetterGearsetUpdateProgress.InProgress;
            }

            case UpdateStage.WaitForTarget:
                if (module->CurrentGearsetIndex == this.targetGearsets[this.targetIndex] && PlayerHelper.IsReadyFull)
                {
                    this.stage = UpdateStage.StartUpdate;
                    return GearsetterGearsetUpdateProgress.InProgress;
                }
                if (Environment.TickCount64 > this.deadline)
                    return this.FailAndRestore($"Timed out equipping gearset {this.targetGearsets[this.targetIndex]}.");
                return GearsetterGearsetUpdateProgress.InProgress;

            case UpdateStage.StartUpdate:
                if (!Gearsetter_IPCSubscriber.IsEnabled)
                    return this.FailAndRestore("Gearsetter became unavailable during the gearset update.");
                if (AutoEquipHelper.State == ActionState.Running)
                    return GearsetterGearsetUpdateProgress.InProgress;
                AutoEquipHelper.Invoke(GearsetUpdateSource.Gearsetter, suppressPortraitUpdate: true);
                this.stage = UpdateStage.WaitForUpdate;
                return GearsetterGearsetUpdateProgress.InProgress;

            case UpdateStage.WaitForUpdate:
                if (AutoEquipHelper.State == ActionState.Running)
                    return GearsetterGearsetUpdateProgress.InProgress;

                this.targetIndex++;
                this.stage = this.targetIndex < this.targetGearsets.Count
                    ? UpdateStage.EquipTarget
                    : UpdateStage.RestoreOriginal;
                return GearsetterGearsetUpdateProgress.InProgress;

            case UpdateStage.RestoreOriginal:
                if (AutoEquipHelper.State == ActionState.Running)
                    return GearsetterGearsetUpdateProgress.InProgress;

                if (module->CurrentGearsetIndex == this.originalGearset)
                {
                    this.stage = UpdateStage.Finished;
                    return this.failed ? GearsetterGearsetUpdateProgress.Failed : GearsetterGearsetUpdateProgress.Complete;
                }

                module->EquipGearset(this.originalGearset);
                this.deadline = Environment.TickCount64 + GearsetSwitchTimeoutMilliseconds;
                this.stage = UpdateStage.WaitForOriginal;
                return GearsetterGearsetUpdateProgress.InProgress;

            case UpdateStage.WaitForOriginal:
                if (module->CurrentGearsetIndex == this.originalGearset && PlayerHelper.IsReadyFull)
                {
                    this.stage = UpdateStage.Finished;
                    return this.failed ? GearsetterGearsetUpdateProgress.Failed : GearsetterGearsetUpdateProgress.Complete;
                }
                if (Environment.TickCount64 > this.deadline)
                {
                    this.failed = true;
                    this.LastError = string.IsNullOrEmpty(this.LastError)
                        ? $"Timed out restoring original gearset {this.originalGearset}."
                        : $"{this.LastError} Also timed out restoring original gearset {this.originalGearset}.";
                    this.stage = UpdateStage.Finished;
                    return GearsetterGearsetUpdateProgress.Failed;
                }
                return GearsetterGearsetUpdateProgress.InProgress;

            default:
                return this.FailAndRestore("Unknown gearset update state.");
        }
    }

    internal void CancelAndRestore()
    {
        if (!this.IsActive)
            return;

        this.failed = true;
        this.LastError = "Gearset update was interrupted.";
        AutoEquipHelper.ForceStop();
        this.stage = UpdateStage.RestoreOriginal;
    }

    internal void Reset()
    {
        this.targetGearsets.Clear();
        this.stage = UpdateStage.None;
        this.targetIndex = 0;
        this.originalGearset = 0;
        this.deadline = 0;
        this.failed = false;
        this.LastError = string.Empty;
    }

    private GearsetterGearsetUpdateProgress FailAndRestore(string error)
    {
        this.failed = true;
        this.LastError = error;
        this.stage = UpdateStage.RestoreOriginal;
        return GearsetterGearsetUpdateProgress.InProgress;
    }
}
