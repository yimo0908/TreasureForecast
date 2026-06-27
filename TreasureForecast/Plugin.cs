using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.DutyState;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using TreasureForecast.Data;
using TreasureForecast.Models;
using TreasureForecast.Utils;
using TreasureForecast.Windows;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TreasureForecast;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;

    private const string CommandName = "/tforecast";

    public Configuration Configuration { get; init; }
    public TreasurePredictionService PredictionService { get; }

    public readonly WindowSystem WindowSystem = new("TreasureForecast");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    /// <summary>
    /// 底层网络数据包接收器（统一管理所有 Hook）
    /// </summary>
    private NetworkReceiver NetworkReceiver { get; }

    internal AchievementTracker AchievementTracker { get; }
    internal List<AchievementProgressInfo> Achievements { get; }

    private ushort _lastCompletedTerritory;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        PredictionService = new TreasurePredictionService();

        // 创建 UI 窗口
        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        // 注册命令
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开挖宝预测主窗口"
        });

        // 注册 UI 绘制
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        // ---- 初始化网络 Hook（统一管理） ----
        NetworkReceiver = new NetworkReceiver(
            GameInteropProvider,
            Log,
            ClientState,
            PredictionService,
            Configuration);
        NetworkReceiver.Initialize();

        // ---- 订阅预测事件 ----
        PredictionService.OnTreasureResult += OnTreasureResult;

        // ---- 初始化成就进度跟踪 ----
        var titleSheet = DataManager.GameData.GetExcelSheet<Title>();
        Achievements = Constants.AchievementIds
            .Select((id, i) =>
            {
                var titleRow = titleSheet?.GetRowOrDefault(
                    DataManager.GameData.GetExcelSheet<Achievement>()?.GetRowOrDefault(id)?.Title.RowId ?? 0);
                return new AchievementProgressInfo
                {
                    AchievementId = id,
                    AchievementName = Constants.TreasureTerritories[i].Name,
                    TitleName = titleRow?.Masculine.ToString() ?? ""
                };
            }).ToList();

        // 构建成就 ID → 索引 字典，O(1) 回调查找
        for (int i = 0; i < Achievements.Count; i++)
            _achIndexById[Achievements[i].AchievementId] = i;

        // 全部成就初始时 Max==0 → 均未初始化
        _uninitializedCount = Achievements.Count;

        AchievementTracker = new AchievementTracker(GameInteropProvider);
        AchievementTracker.OnAchievementProgress += OnAchievementProgress;

        // 每帧检查未收到数据的成就，自动重发请求
        Framework.Update += OnFrameworkUpdate;

        // ---- 订阅副本完成事件 ----
        DutyState.DutyCompleted += OnDutyCompleted;
        DutyState.DutyStarted += OnDutyStarted;

        Log.Information($"=== {PluginInterface.Manifest.Name} 已加载 ===");
        Log.Information($"配置: Wheel={Configuration.EnableWheelPrediction}, Gate={Configuration.EnableGatePrediction}, " +
                        $"Hypnoslot={Configuration.EnableHypnoslot}, ShowInChat={Configuration.ShowInChat}");
    }

    public void Dispose()
    {
        NetworkReceiver.Dispose();

        DutyState.DutyCompleted -= OnDutyCompleted;
        DutyState.DutyStarted -= OnDutyStarted;

        AchievementTracker.OnAchievementProgress -= OnAchievementProgress;
        AchievementTracker.Dispose();
        Framework.Update -= OnFrameworkUpdate;

        PredictionService.OnTreasureResult -= OnTreasureResult;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    // 处理预测结果
    private void OnTreasureResult(TreasureResultDTO dto)
    {
        try
        {
            if (dto == null) return;

            // 根据配置过滤不同来源/类型的预测
            if (dto.Value.StartsWith("wheel-"))
            {
                // 巡梦金库的转盘使用 Hypnoslot 开关
                if (dto.Source == "巡梦金库")
                {
                    if (!Configuration.EnableHypnoslot) return;
                }
                else
                {
                    if (!Configuration.EnableWheelPrediction) return;
                }
            }
            else if (dto.Value.StartsWith("gate-"))
            {
                if (!Configuration.EnableGatePrediction) return;
            }

            // 更新主窗口历史与统计
            MainWindow.AddResult(dto);

            // 获取结果文本
            var text = ResultFormatter.GetTreasureResultText(dto.Value);

            // 显示游戏内提示（GimmickHint）
            if (Configuration.ShowToastResult)
            {
                var isFailure = text is "失败" or "召唤失败";
                ShowGimmickHint(
                    text,
                    isFailure ? RaptureAtkModule.TextGimmickHintStyle.Warning : RaptureAtkModule.TextGimmickHintStyle.Info);
            }

            // 在聊天框显示（如果已开启）
            if (Configuration.ShowInChat)
            {
                var source = string.IsNullOrEmpty(dto.Source) ? "挖宝预测" : dto.Source;
                var roundInfo = dto.Round > 0 ? $" (第{dto.Round}轮)" : "";
                Chat.Print($"[{source}] {text}{roundInfo}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "处理挖宝结果时出错");
        }
    }

    private int _achRetryCounter;
    private int _nextUninitializedIdx;
    private int _uninitializedCount;
    private readonly Queue<int> _pendingRefresh = new();
    private readonly Dictionary<uint, int> _achIndexById = new();

    private const int AchInitRetryInterval = 30;   // 未初始化时每 0.5s 快速重试
    private const int AchRefreshInterval = 300;     // 全部初始化后每 5s 批量刷新

    private void OnAchievementProgress(uint id, uint current, uint max)
    {
        if (_achIndexById.TryGetValue(id, out var idx))
        {
            var entry = Achievements[idx];
            // 首次从 Max==0 变为 Max>0 → 未初始化计数减一
            if (entry.Max == 0 && max > 0)
                _uninitializedCount--;
            entry.Current = current;
            entry.Max = max;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Phase 1: 逐帧消费待刷新队列，避免 ProgressRequestState 锁冲突
        if (_pendingRefresh.Count > 0)
        {
            var idx = _pendingRefresh.Dequeue();
            AchievementTracker.Request(Achievements[idx].AchievementId);
            return;
        }

        _achRetryCounter++;

        // 用计数器 O(1) 判断是否全部已初始化，替代每帧线性扫描
        var allInit = _uninitializedCount == 0;

        if (allInit)
        {
            // Phase 2: 全部已初始化 → 每 AchRefreshInterval 帧批量排入刷新队列
            if (_achRetryCounter >= AchRefreshInterval)
            {
                _achRetryCounter = 0;
                for (int i = 0; i < Achievements.Count; i++)
                    _pendingRefresh.Enqueue(i);
                // 本帧立即消费一个
                var idx = _pendingRefresh.Dequeue();
                AchievementTracker.Request(Achievements[idx].AchievementId);
            }
        }
        else
        {
            // Phase 3: 存在未初始化成就 → 每 AchInitRetryInterval 帧快速重试（轮询扫描）
            if (_achRetryCounter >= AchInitRetryInterval)
            {
                _achRetryCounter = 0;
                for (int i = 0; i < Achievements.Count; i++)
                {
                    int idx = (_nextUninitializedIdx + i) % Achievements.Count;
                    if (Achievements[idx].Max == 0)
                    {
                        AchievementTracker.Request(Achievements[idx].AchievementId);
                        _nextUninitializedIdx = (idx + 1) % Achievements.Count;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 在游戏屏幕上显示 Gimmick 提示（屏幕中央偏上位置的气泡提示）
    /// </summary>
    /// <param name="text">显示文本</param>
    /// <param name="style">提示样式</param>
    /// <param name="duration">显示时长（秒）</param>
    private unsafe void ShowGimmickHint(
        string text,
        RaptureAtkModule.TextGimmickHintStyle style = RaptureAtkModule.TextGimmickHintStyle.Info,
        int duration = 5)
    {
        try
        {
            var raptureAtkModule = RaptureAtkModule.Instance();
            if (raptureAtkModule == null) return;

            raptureAtkModule->ShowTextGimmickHint(text, style, duration);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "显示 GimmickHint 时出错");
        }
    }

    public void ExportAchievementProgress()
    {
        var lines = Achievements.Select(a => $"{a.AchievementName}: {a.Current}/{a.Max}");
        var text = string.Join("\n", lines);
        ImGui.SetClipboardText(text);
        Chat.Print("成就进度导出成功");
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        _lastCompletedTerritory = 0;
        PredictionService.ClearCurrentMapName();
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        var territoryId = (ushort)args.TerritoryType.RowId;
        if (!Constants.TerritoryIdSet.Contains(territoryId)) return;
        if (territoryId == _lastCompletedTerritory) return;
        _lastCompletedTerritory = territoryId;

        if (Configuration.ShowDungeonCompleteMessage)
        {
            Chat.Print("❀❀下底成功❀❀");
            MainWindow.AddDutyCompleteSeparator();
        }
        PredictionService.ClearCurrentMapName();
    }

    private void OnCommand(string command, string args)
    {
        // 支持: /tforecast [config]
        if (string.IsNullOrWhiteSpace(args))
        {
            ToggleMainUi();
            return;
        }
        var arg = args.Trim().ToLowerInvariant();
        if (arg == "config" || arg == "cfg")
            ToggleConfigUi();
        else
            ToggleMainUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
