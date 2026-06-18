using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TreasureForecast.Models;
using TreasureForecast.Utils;
using TreasureForecast.Windows;
using System;
using System.Runtime.InteropServices;

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

    // 网络 Hook 相关
    private Dalamud.Hooking.Hook<HandleActorControlPacketDelegate>? actorControlHook;
    private int _actorControlPacketCount;

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

        // ---- 订阅网络事件（API 15: 使用 Hook 方式） ----
        InitializeNetworkHook();

        // ---- 订阅预测事件 ----
        PredictionService.OnTreasureResult += OnTreasureResult;

        Log.Information($"=== {PluginInterface.Manifest.Name} 已加载 ===");
        Log.Information($"配置: Wheel={Configuration.EnableWheelPrediction}, Gate={Configuration.EnableGatePrediction}, " +
                        $"Hypnoslot={Configuration.EnableHypnoslot}, ShowInChat={Configuration.ShowInChat}, ShowHistory={Configuration.ShowHistory}");
    }

    public void Dispose()
    {
        actorControlHook?.Disable();
        actorControlHook?.Dispose();

        PredictionService.OnTreasureResult -= OnTreasureResult;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    /// <summary>
    /// 初始化网络数据包 Hook（API 15+ 方式：Hook HandleActorControlPacket）
    /// </summary>
    private void InitializeNetworkHook()
    {
        nint addr = ResolveActorControlAddress();
        if (addr != nint.Zero)
        {
            Log.Information($"成功获取 HandleActorControlPacket 地址: 0x{addr:X}");
            actorControlHook = GameInteropProvider.HookFromAddress<HandleActorControlPacketDelegate>(
                addr, OnActorControlPacket);
            actorControlHook.Enable();
            Log.Information("HandleActorControlPacket Hook 已启用");
        }
        else
        {
            Log.Warning("无法解析 HandleActorControlPacket 地址！");
        }
    }

    private static nint ResolveActorControlAddress()
    {
        var typeName = "FFXIVClientStructs.FFXIV.Client.Network.PacketDispatcher, FFXIVClientStructs";
        var pdType = Type.GetType(typeName);
        if (pdType == null) return nint.Zero;

        var nested = pdType.GetNestedType("Addresses", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (nested == null) return nint.Zero;

        var field = nested.GetField("HandleActorControlPacket", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (field == null) return nint.Zero;

        var val = field.GetValue(null);
        return UnwrapAddress(val);
    }



    /// <summary>
    /// 从反射得到的对象中提取 nint 地址
    /// 兼容：直接 nint、IntPtr、以及带 .Value 属性/字段的 Address 包装器
    /// </summary>
    private static nint UnwrapAddress(object? val)
    {
        if (val == null) return nint.Zero;
        if (val is nint ni) return ni;
        if (val is IntPtr ip) return (nint)ip;

        // FFXIVClientStructs 的 Address 类型：Value 可能是属性或字段
        var t = val.GetType();
        try
        {
            // 尝试属性
            var valueProp = t.GetProperty("Value");
            if (valueProp != null)
            {
                var inner = valueProp.GetValue(val);
                if (inner is nint ni2) return ni2;
                if (inner is IntPtr ip2) return (nint)ip2;
            }

            // 尝试字段（InteropGenerator.Runtime.Address 使用字段存储 IntPtr）
            var valueField = t.GetField("Value", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (valueField != null)
            {
                var inner = valueField.GetValue(val);
                if (inner is nint ni3) return ni3;
                if (inner is IntPtr ip3) return (nint)ip3;
            }
        }
        catch { }

        return nint.Zero;
    }

    /// <summary>
    /// HandleActorControlPacket 的委托签名（匹配 FFXIVClientStructs 生成的定义）
    /// (UInt32 entityId, UInt32 category, UInt32 arg1..arg8, GameObjectId targetId, Boolean isRecorded)
    /// GameObjectId = UInt64
    /// </summary>
    private unsafe delegate void HandleActorControlPacketDelegate(
        uint entityId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetId, bool isRecorded);

    /// <summary>
    /// HandleActorControlPacket 的 Detour — 处理 ActorControl 数据包
    /// </summary>
    private void OnActorControlPacket(
        uint entityId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetId, bool isRecorded)
    {
        // 调用原始函数
        actorControlHook!.Original(
            entityId, category,
            arg1, arg2, arg3, arg4,
            arg5, arg6, arg7, arg8,
            targetId, isRecorded);

        try
        {
            var territoryId = (ushort)ClientState.TerritoryType;

            // ---- 日志：定期输出 ActorControl 摘要（诊断用，每50条输出一次） ----
            _actorControlPacketCount++;
            if (_actorControlPacketCount % 50 == 0 && category > 0)
            {
                Log.Info($"[诊断] ActorControl #{_actorControlPacketCount}: cat={category} a1={arg1} a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
            }

            // ---- 巡梦金库老虎机 (category = 407, 仅在 territory 1279) ----
            if (category == 407 && territoryId == 1279)
            {
                switch ((HypnoslotResultType)arg1)
                {
                    case HypnoslotResultType.AllDiff:
                    case HypnoslotResultType.AllSame:
                    case HypnoslotResultType.Reroll:
                        var openDto = new TreasureResultDTO
                        {
                            Value = "wheel-open",
                            Source = "巡梦金库"
                        };
                        OnTreasureResult(openDto);
                        break;
                    case HypnoslotResultType.End:
                        var endDto = new TreasureResultDTO
                        {
                            Value = "wheel-end",
                            Source = "巡梦金库"
                        };
                        OnTreasureResult(endDto);
                        break;
                }
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "处理 ActorControl 数据包时出错");
        }
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

            // 在聊天框显示（如果已开启）
            if (Configuration.ShowInChat)
            {
                var text = ResultFormatter.GetTreasureResultText(dto.Value);
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
