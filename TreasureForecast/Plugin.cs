using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using TreasureForecast.Models;
using TreasureForecast.Utils;
using TreasureForecast.Windows;
using System;

namespace TreasureForecast;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

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

        Log.Information($"=== {PluginInterface.Manifest.Name} 已加载 ===");
        Log.Information($"配置: Wheel={Configuration.EnableWheelPrediction}, Gate={Configuration.EnableGatePrediction}, " +
                        $"Hypnoslot={Configuration.EnableHypnoslot}, ShowInChat={Configuration.ShowInChat}, ShowHistory={Configuration.ShowHistory}");
    }

    public void Dispose()
    {
        NetworkReceiver.Dispose();

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
            // 结果为失败时使用 Warning 样式
            var isFailure = text is "失败" or "召唤失败";
            ShowGimmickHint(
                text,
                isFailure ? RaptureAtkModule.TextGimmickHintStyle.Warning : RaptureAtkModule.TextGimmickHintStyle.Info);

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
