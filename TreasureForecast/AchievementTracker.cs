using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace TreasureForecast;

public unsafe class AchievementTracker : IDisposable
{
    public event Action<uint, uint, uint>? OnAchievementProgress;

    private readonly Hook<Achievement.Delegates.ReceiveAchievementProgress> _hook;

    public AchievementTracker(IGameInteropProvider hook)
    {
        _hook = hook.HookFromAddress<Achievement.Delegates.ReceiveAchievementProgress>(
            Achievement.Addresses.ReceiveAchievementProgress.Value,
            ReceiveAchievementDetour);
        _hook.Enable();
    }

    public void Dispose()
    {
        _hook.Dispose();
    }

    public void Request(uint id)
    {
        var ui = UIState.Instance();
        if (ui->PlayerState.IsLoaded &&
            ui->Achievement.ProgressRequestState != Achievement.AchievementState.Requested)
            ui->Achievement.RequestAchievementProgress(id);
    }

    private void ReceiveAchievementDetour(Achievement* self, uint id, uint current, uint max)
    {
        OnAchievementProgress?.Invoke(id, current, max);
        _hook.Original(self, id, current, max);
    }
}
