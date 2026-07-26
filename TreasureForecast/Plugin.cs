using Dalamud.Game.Chat;
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

    private ushort lastCompletedTerritory;
    private bool wasInTreasureTerritory;

    // ---- 选门开门地图状态追踪 ----
    private bool wasInDoorSelectionMap;
    private string? doorSelectionMapName;
    private bool gateOpenReceived;
    private bool gateFailReceived;
    private bool dutyWipedOrCompleted;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        PredictionService = new TreasurePredictionService();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "打开挖宝预测主窗口"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        NetworkReceiver = new NetworkReceiver(
            GameInteropProvider,
            Log,
            ClientState,
            PredictionService,
            Configuration);
        NetworkReceiver.Initialize();

        Chat.LogMessage += OnLogMessage;

        PredictionService.OnTreasureResult += OnTreasureResult;

        var titleSheet = DataManager.GameData.GetExcelSheet<Title>();
        var achievementSheet = DataManager.GameData.GetExcelSheet<Achievement>();
        Achievements = Constants.AchievementIDs
            .Select((id, i) =>
            {
                var titleRowId = achievementSheet?.GetRowOrDefault(id)?.Title.RowId ?? 0;
                var titleRow = titleSheet?.GetRowOrDefault(titleRowId);
                return new AchievementProgressInfo
                {
                    AchievementID = id,
                    TitleID = titleRowId,
                    AchievementName = Constants.TreasureTerritories[i].Name,
                    TitleName = titleRow?.Masculine.ToString() ?? ""
                };
            }).ToList();

        // 构建成就 ID → 索引 字典，O(1) 回调查找
        for (int i = 0; i < Achievements.Count; i++)
            achIndexByID[Achievements[i].AchievementID] = i;

        // 全部成就初始时 Max==0 → 均未初始化
        uninitializedCount = Achievements.Count;

        AchievementTracker = new AchievementTracker(GameInteropProvider);
        AchievementTracker.OnAchievementProgress += OnAchievementProgress;

        // 每帧检查未收到数据的成就，自动重发请求
        Framework.Update += OnFrameworkUpdate;

        DutyState.DutyCompleted += OnDutyCompleted;
        DutyState.DutyStarted += OnDutyStarted;
        DutyState.DutyWiped += OnDutyWiped;

        // 以领地变动为触发：从挖宝地图出来后添加分割线
        wasInTreasureTerritory = Constants.TerritoryIDSet.Contains((ushort)ClientState.TerritoryType);
        ClientState.TerritoryChanged += OnTerritoryChanged;

        Log.Information($"=== {PluginInterface.Manifest.Name} 已加载 ===");
        Log.Information($"配置: Wheel={Configuration.EnableWheelPrediction}, Gate={Configuration.EnableGatePrediction}, " +
                        $"Hypnoslot={Configuration.EnableHypnoslot}, ShowInChat={Configuration.ShowInChat}");
    }

    public void Dispose()
    {
        Chat.LogMessage -= OnLogMessage;
        NetworkReceiver.Dispose();

        DutyState.DutyCompleted -= OnDutyCompleted;
        DutyState.DutyStarted -= OnDutyStarted;
        DutyState.DutyWiped -= OnDutyWiped;

        ClientState.TerritoryChanged -= OnTerritoryChanged;

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

    private void OnTreasureResult(TreasureResultDTO dto)
    {
        try
        {
            if (dto == null) return;

            // 追踪选门开门/失败状态
            if (dto.Value == "gate-open")
                gateOpenReceived = true;
            else if (dto.Value == "gate-fail")
                gateFailReceived = true;

            // 去重：若 AddResult 返回 false（与上一条重复），跳过播报
            if (!MainWindow.AddResult(dto)) return;

            var text = ResultFormatter.GetTreasureResultText(dto.Value);

            if (Configuration.ShowToastResult)
            {
                var isFailure = text is "失败" or "召唤失败";
                ShowGimmickHint(
                    text,
                    isFailure ? RaptureAtkModule.TextGimmickHintStyle.Warning : RaptureAtkModule.TextGimmickHintStyle.Info);
            }

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

    private int achRetryCounter;
    private int nextUninitializedIdx;
    private int uninitializedCount;
    private readonly Queue<int> pendingRefresh = new();
    private readonly Dictionary<uint, int> achIndexByID = new();

    private const int AchInitRetryInterval = 30;   // 未初始化时每 0.5s 快速重试
    private const int AchRefreshInterval = 300;     // 全部初始化后每 5s 批量刷新

    private void OnAchievementProgress(uint id, uint current, uint max)
    {
        if (achIndexByID.TryGetValue(id, out var idx))
        {
            var entry = Achievements[idx];
            // 首次从 Max==0 变为 Max>0 → 未初始化计数减一
            if (entry.Max == 0 && max > 0)
                uninitializedCount--;
            entry.Current = current;
            entry.Max = max;
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // Phase 1: 逐帧消费待刷新队列，避免 ProgressRequestState 锁冲突
        if (pendingRefresh.Count > 0)
        {
            var idx = pendingRefresh.Dequeue();
            AchievementTracker.Request(Achievements[idx].AchievementID);
            return;
        }

        achRetryCounter++;

        // 用计数器 O(1) 判断是否全部已初始化，替代每帧线性扫描
        var allInit = uninitializedCount == 0;

        if (allInit)
        {
            // Phase 2: 全部已初始化 → 每 AchRefreshInterval 帧批量排入刷新队列
            if (achRetryCounter >= AchRefreshInterval)
            {
                achRetryCounter = 0;
                for (int i = 0; i < Achievements.Count; i++)
                    pendingRefresh.Enqueue(i);
                // 本帧立即消费一个
                var idx = pendingRefresh.Dequeue();
                AchievementTracker.Request(Achievements[idx].AchievementID);
            }
        }
        else
        {
            // Phase 3: 存在未初始化成就 → 每 AchInitRetryInterval 帧快速重试（轮询扫描）
            if (achRetryCounter >= AchInitRetryInterval)
            {
                achRetryCounter = 0;
                for (int i = 0; i < Achievements.Count; i++)
                {
                    int idx = (nextUninitializedIdx + i) % Achievements.Count;
                    if (Achievements[idx].Max == 0)
                    {
                        AchievementTracker.Request(Achievements[idx].AchievementID);
                        nextUninitializedIdx = (idx + 1) % Achievements.Count;
                        return;
                    }
                }
            }
        }
    }

    private unsafe void ShowGimmickHint(
        string text,
        RaptureAtkModule.TextGimmickHintStyle style = RaptureAtkModule.TextGimmickHintStyle.Info,
        int duration = 5)
    {
        var module = RaptureAtkModule.Instance();
        if (module != null)
            module->ShowTextGimmickHint(text, style, duration);
    }

    public void ExportAchievementProgress()
    {
        var lines = Achievements.Select(a => $"{a.AchievementName}: {a.Current}/{a.Max}");
        var text = string.Join("\n", lines);
        ImGui.SetClipboardText(text);
        Chat.Print("成就进度导出成功");
    }

    /// <summary>
    /// 设置玩家称号：称号列表已加载时检查是否解锁，未加载时直接发送请求（由服务端校验）。
    /// </summary>
    public unsafe void SetTitle(uint titleId, string titleName)
    {
        if (titleId == 0) return;

        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (ui == null || !ui->PlayerState.IsLoaded) return;

        if (ui->TitleList.DataReceived)
        {
            if (!ui->TitleList.IsTitleUnlocked((ushort)titleId))
            {
                Chat.PrintError("该称号尚未解锁");
                return;
            }
        }
        else
        {
            ui->TitleList.RequestTitleList();
        }

        ui->TitleController.SendTitleIdUpdate((ushort)titleId);
        Chat.Print($"称号已设置: {titleName}");
    }

    private void OnDutyStarted(IDutyStateEventArgs args)
    {
        lastCompletedTerritory = 0;
        ResetDoorSelectionState();
        PredictionService.ClearCurrentMapName();
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        dutyWipedOrCompleted = true;

        var territoryID = (ushort)args.TerritoryType.RowId;
        if (!Constants.TerritoryIDSet.Contains(territoryID)) return;
        if (territoryID == lastCompletedTerritory) return;
        lastCompletedTerritory = territoryID;

        if (Configuration.ShowDungeonCompleteMessage)
            MainWindow.AddDutyEventEntry("dungeon-complete");
        PredictionService.ClearCurrentMapName();
    }

    /// <summary>
    /// 领地变动时检测：
    /// 1. 从选门地图退出且无失败网络包/团灭/完成 → 补记失败记录
    /// 2. 从挖宝地图出来 → 添加历史分割线
    /// </summary>
    private void OnTerritoryChanged(uint territoryID)
    {
        var isTreasure = Constants.TerritoryIDSet.Contains((ushort)territoryID);
        var isDoorSelection = Constants.DoorSelectionTerritoryIds.Contains((ushort)territoryID);

        // 从选门地图退出：检查是否需要补记失败记录
        if (wasInDoorSelectionMap && !isDoorSelection)
            TryFallbackFailRecord();

        // 从挖宝地图出来（宝藏领地 → 非宝藏领地）时添加分割线
        if (wasInTreasureTerritory && !isTreasure)
            MainWindow.AddSeparator();

        // 更新选门地图状态
        if (isDoorSelection)
        {
            wasInDoorSelectionMap = true;
            doorSelectionMapName = GetTerritoryName((ushort)territoryID);
            ResetDoorSelectionState();
        }
        else
        {
            wasInDoorSelectionMap = false;
            doorSelectionMapName = null;
        }

        wasInTreasureTerritory = isTreasure;
    }

    /// <summary>
    /// 团灭事件：标记当前会话已发生团灭（退出地图时不再补记失败），
    /// 若在挖宝地图中则写入历史记录。
    /// </summary>
    private void OnDutyWiped(IDutyStateEventArgs args)
    {
        dutyWipedOrCompleted = true;

        var territoryID = (ushort)args.TerritoryType.RowId;
        if (!Constants.TerritoryIDSet.Contains(territoryID)) return;

        if (Configuration.ShowDungeonCompleteMessage)
            MainWindow.AddDutyEventEntry("duty-wiped");
    }

    /// <summary>
    /// 选门地图 logmessage 回退：当 IChatGui.LogMessage 事件收到"打开了通往第{n}区的大门"时，
    /// 静默新增一条开门历史记录（不播报，去重对比上一条）。
    /// 轮数从 logmessage 的第 0 个整数参数获取（Case(1)→第二区→round=1，…）。
    /// </summary>
    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            if (!Constants.DoorOpenLogMessageIds.Contains(message.LogMessageId)) return;

            var territoryID = (ushort)ClientState.TerritoryType;
            if (!Constants.DoorSelectionTerritoryIds.Contains(territoryID)) return;

            if (!message.TryGetIntParameter(0, out var value) || value < 1) return;

            var round = value;
            var mapName = GetTerritoryName(territoryID);

            var added = MainWindow.AddResult(new TreasureResultDTO
            {
                Value = "gate-open",
                Source = mapName,
                Round = round,
                Timestamp = DateTime.Now
            });
            if (added)
            {
                gateOpenReceived = true;
                Log.Information($"[选门回退] LogMessage 开门记录已添加: {mapName} 第{round}轮 (logMsgId={message.LogMessageId})");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "处理选门 logmessage 回退时出错");
        }
    }

    /// <summary>
    /// 获取领地名称，未知领地返回 null。
    /// </summary>
    private static string? GetTerritoryName(ushort territoryID) =>
        Constants.TerritoryNameByID.TryGetValue(territoryID, out var name) ? name : null;

    /// <summary>
    /// 重置选门地图状态（开门/失败标志 + 团灭/完成标志）。
    /// </summary>
    private void ResetDoorSelectionState()
    {
        gateOpenReceived = false;
        gateFailReceived = false;
        dutyWipedOrCompleted = false;
    }

    /// <summary>
    /// 退出选门地图时的失败补记：在无失败网络包且未团灭/完成时补记 gate-fail。
    /// 收到过开门记录时补记下一轮失败；未收到任何开门记录（第一轮即失败且无进入动画）时补记第1轮失败。
    /// </summary>
    private void TryFallbackFailRecord()
    {
        if (gateFailReceived || dutyWipedOrCompleted) return;

        int failRound;
        if (gateOpenReceived)
        {
            // 正常回退：上一条是开门记录，补记下一条失败
            failRound = MainWindow.GetLastNonSeparatorRound() + 1;
        }
        else
        {
            // 第一轮即失败且无进入动画（无预测网络包、无开门 logmessage）→ 补记第1轮
            failRound = 1;
        }

        var added = MainWindow.AddResult(new TreasureResultDTO
        {
            Value = "gate-fail",
            Source = doorSelectionMapName,
            Round = failRound,
            Timestamp = DateTime.Now
        });
        if (added)
            Log.Information($"[选门回退] 退出地图补记失败: {doorSelectionMapName} 第{failRound}轮 (gateOpen={gateOpenReceived})");
    }

    private void OnCommand(string command, string args)
    {
        // /tforecast [config]
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
