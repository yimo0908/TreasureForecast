using Dalamud.Plugin.Services;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using TreasureForecast.Data;

namespace TreasureForecast;

internal unsafe class NetworkReceiver : IDisposable
{
    private readonly IGameInteropProvider gameInterop;
    private readonly IPluginLog log;
    private readonly IClientState clientState;
    private readonly TreasurePredictionService predictionService;
    private readonly Configuration configuration;

    internal delegate void HandleActorControlPacketDelegate(
        uint entityID, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetID, bool isRecorded);

    private delegate void OnReceivePacketDelegate(nint dispatcher, uint targetID, nint packetPtr);

    private enum HypnoslotResultType : byte
    {
        AllDiff = 156,
        AllSame = 157,
        Preserve = 158,
        Reroll = 159,
        End = 160,
        Resume = 161,
    }

    private enum ShiftingWheelResultType : byte
    {
        Low = 191,
        Medium = 192,
        High = 193,
        Shift = 194,
        Special = 195,
        End = 196,
    }

    private Dalamud.Hooking.Hook<HandleActorControlPacketDelegate>? actorControlHook;
    private Dalamud.Hooking.Hook<OnReceivePacketDelegate>? onReceivePacketHook;
    private Dalamud.Hooking.Hook<ShowLogMessageUIntDelegate>? showLogMessageUIntHook;

    /// <summary>
    /// 选门地图 logmessage 回调：当 ShowLogMessageUInt 被调用且 logMessageID 为"开门"消息时触发。
    /// 参数为轮数（即 ShowLogMessageUInt 的 value 参数）。
    /// </summary>
    internal event Action<int>? OnDoorGateOpenLogMessage;

    private delegate void ShowLogMessageUIntDelegate(nint raptureLogModule, uint logMessageID, uint value);

    private int actorControlPacketCount;

    private ushort lastTerritoryID;
    private bool isTreasureTerritory;

    /// <summary>当前领地 ID（ushort），消除多处重复 cast。</summary>
    private ushort CurrentTerritoryID => (ushort)clientState.TerritoryType;

    // packetPtr 可能指向纯负载 / 16B 头+负载 / 32B 头+负载，按候选偏移量试探匹配。
    // level 值和 flag 非常特异，误匹配概率极低。
    private static readonly int[] CandidateBodyOffsets = { 0x00, 0x10, 0x20 };

    private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.Static;

    public NetworkReceiver(
        IGameInteropProvider gameInterop,
        IPluginLog log,
        IClientState clientState,
        TreasurePredictionService predictionService,
        Configuration configuration)
    {
        this.gameInterop = gameInterop;
        this.log = log;
        this.clientState = clientState;
        this.predictionService = predictionService;
        this.configuration = configuration;
    }

    public void Initialize()
    {
        var actorControlAddr = ResolveActorControlAddress();
        if (actorControlAddr != nint.Zero)
        {
            log.Information($"成功获取 HandleActorControlPacket 地址: 0x{actorControlAddr:X}");
            actorControlHook = gameInterop.HookFromAddress<HandleActorControlPacketDelegate>(
                actorControlAddr, OnActorControlPacket);
            actorControlHook.Enable();
        }
        else
        {
            log.Warning("无法解析 HandleActorControlPacket 地址！巡梦金库预测不可用。");
        }

        var receivePacketAddr = ResolveOnReceivePacketAddress();
        if (receivePacketAddr != nint.Zero)
        {
            log.Information($"成功获取 OnReceivePacket 地址: 0x{receivePacketAddr:X}");
            onReceivePacketHook = gameInterop.HookFromAddress<OnReceivePacketDelegate>(
                receivePacketAddr, OnReceivePacket);
            onReceivePacketHook.Enable();
        }
        else
        {
            log.Warning("无法解析 OnReceivePacket 地址！G10/G12/G15 转盘和开门预测不可用。");
        }

        try
        {
            showLogMessageUIntHook = gameInterop.HookFromSignature<ShowLogMessageUIntDelegate>(
                "E9 ?? ?? ?? ?? 0C ?? 88 42", OnShowLogMessageUInt);
            showLogMessageUIntHook.Enable();
            log.Information("成功 Hook ShowLogMessageUInt");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "无法 Hook ShowLogMessageUInt！选门地图 logmessage 回退不可用。");
        }
    }

    public void Dispose()
    {
        showLogMessageUIntHook?.Disable();
        showLogMessageUIntHook?.Dispose();
        onReceivePacketHook?.Disable();
        onReceivePacketHook?.Dispose();
        actorControlHook?.Disable();
        actorControlHook?.Dispose();
    }

    private static Type? GetAddressesType()
    {
        var pdType = Type.GetType("FFXIVClientStructs.FFXIV.Client.Network.PacketDispatcher, FFXIVClientStructs");
        return pdType?.GetNestedType("Addresses", StaticFlags);
    }

    private static nint ResolveActorControlAddress()
    {
        var addresses = GetAddressesType();
        if (addresses == null) return nint.Zero;

        var field = addresses.GetField("HandleActorControlPacket", StaticFlags);
        if (field == null) return nint.Zero;

        return UnwrapAddress(field.GetValue(null));
    }

    private static nint ResolveOnReceivePacketAddress()
    {
        var addresses = GetAddressesType();
        if (addresses == null) return nint.Zero;

        // [VirtualTable] 结构生成 VirtualTable 或 StaticVirtualTable 字段
        var vtableField = addresses.GetField("VirtualTable", StaticFlags)
            ?? addresses.GetField("StaticVirtualTable", StaticFlags);
        if (vtableField == null) return nint.Zero;

        var vtableAddr = UnwrapAddress(vtableField.GetValue(null));
        if (vtableAddr == nint.Zero) return nint.Zero;

        // OnReceivePacket 是 VirtualFunction(1) → vtable[1]
        return Marshal.ReadIntPtr(vtableAddr + nint.Size);
    }

    private static nint UnwrapAddress(object? val)
    {
        if (val is nint ni) return ni;

        var t = val?.GetType();
        if (t == null) return nint.Zero;

        var inner = t.GetProperty("Value")?.GetValue(val)
                    ?? t.GetField("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(val);
        return inner is nint ni2 ? ni2 : nint.Zero;
    }

    private void TrySetCurrentMapName()
    {
        var territoryID = CurrentTerritoryID;

        // 领地未变化：仅在地图名被 wheel-end/dungeon-complete 清除后重新设置
        if (territoryID == lastTerritoryID)
        {
            if (isTreasureTerritory && !predictionService.HasCurrentMapName)
            {
                predictionService.SetCurrentMapName(Constants.TerritoryNameByID[territoryID]);
            }
            return;
        }

        // 领地变化：更新缓存与宝藏领地标记
        lastTerritoryID = territoryID;
        if (Constants.TerritoryNameByID.TryGetValue(territoryID, out var name))
        {
            isTreasureTerritory = true;
            predictionService.SetCurrentMapName(name);
        }
        else
        {
            isTreasureTerritory = false;
        }
    }

    private void OnActorControlPacket(
        uint entityID, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetID, bool isRecorded)
    {
        actorControlHook!.Original(entityID, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetID, isRecorded);

        try
        {
            TrySetCurrentMapName();

            var territoryID = CurrentTerritoryID;

            actorControlPacketCount++;
            if (configuration.EnableDebugLog && isTreasureTerritory && actorControlPacketCount % 50 == 0 && category > 0)
            {
                log.Debug($"[诊断] ActorControl #{actorControlPacketCount}: cat={category} a1={arg1} a2={arg2} a3={arg3} a4={arg4} terr={territoryID}");
            }

            // ---- cat=407 诊断日志 (仅 Debug 模式, 仅挖宝地图) ----
            if (configuration.EnableDebugLog && category == 407 && isTreasureTerritory)
            {
                var resultByte = (byte)arg1;
                var enumName = Enum.IsDefined(typeof(HypnoslotResultType), resultByte)
                    ? ((HypnoslotResultType)resultByte).ToString()
                    : $"Unknown(0x{resultByte:X2})";
                log.Information($"[ActorControl|407] arg1={arg1} byte={resultByte} ({enumName}) a2={arg2} a3={arg3} a4={arg4} terr={territoryID}");
            }

            // ---- 巡梦金库老虎机 (category = 407, 仅在 territory 1279) ----
            if (category == 407 && territoryID == 1279 && configuration.EnableHypnoslot)
            {
                var result = (HypnoslotResultType)arg1;
                switch (result)
                {
                    case HypnoslotResultType.AllDiff:
                    case HypnoslotResultType.AllSame:
                    case HypnoslotResultType.Preserve:
                    case HypnoslotResultType.Reroll:
                    case HypnoslotResultType.Resume:
                        predictionService.ProduceResult("wheel-open", "巡梦金库", 0);
                        break;
                    case HypnoslotResultType.End:
                        predictionService.ProduceResult("wheel-end", "巡梦金库", 0);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "处理 ActorControl 数据包时出错");
        }
    }

    private void OnReceivePacket(nint dispatcher, uint targetID, nint packetPtr)
    {
        onReceivePacketHook!.Original(dispatcher, targetID, packetPtr);

        try
        {
            TrySetCurrentMapName();

            if (packetPtr == nint.Zero) return;

            TryMatchPacket((byte*)packetPtr);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "处理 OnReceivePacket 数据包时出错");
        }
    }

    private void TryMatchPacket(byte* rawData)
    {
        var checkWheel = configuration.EnableWheelPrediction;
        var checkGate = configuration.EnableGatePrediction;
        if (!checkWheel && !checkGate) return;

        foreach (var bodyOff in CandidateBodyOffsets)
        {
            // 转盘匹配: body[24] = level (uint), body[40] = resultType (byte)
            if (checkWheel)
            {
                var level = *(uint*)(rawData + bodyOff + 24);

                string? source = level switch
                {
                    7636061 => "G10 运河宝物库神殿",
                    8508181 => "G12 梦羽宝殿",
                    9413549 => "G15 育体宝殿",
                    _ => null
                };

                if (source != null)
                {
                    byte resultByte = *(rawData + bodyOff + 40);
                    var result = (ShiftingWheelResultType)resultByte;
                    string? value = result switch
                    {
                        ShiftingWheelResultType.Low     => "wheel-low",
                        ShiftingWheelResultType.Medium  => "wheel-medium",
                        ShiftingWheelResultType.High    => "wheel-high",
                        ShiftingWheelResultType.Shift   => "wheel-shift",
                        ShiftingWheelResultType.Special => "wheel-special",
                        ShiftingWheelResultType.End     => "wheel-end",
                        _ => null
                    };

                    if (value != null)
                    {
                        log.Information($"[挖宝预测] 转盘结果: {source} → {value} (bodyOff=0x{bodyOff:X2})");
                        if (configuration.EnableDebugLog)
                        {
                            log.Debug($"[诊断] 转盘匹配 hex[0x00..0x50]: {DumpPacketHex(rawData)} | level@0x{bodyOff + 24:X2}={level} result@0x{bodyOff + 40:X2}=0x{resultByte:X2}({value})");
                        }
                        predictionService.ProduceResult(value, source, 0);
                        return;
                    }

                    log.Warning($"[诊断] 转盘近似匹配: level={level}({source}) bodyOff=0x{bodyOff:X2} 但 resultByte=0x{resultByte:X2} 未知，hex[0x00..0x50]: {DumpPacketHex(rawData)}");
                }
            }

            // 开门匹配: body[16] = flag (0x04482c03), body[32] = round, body[40] = result
            if (checkGate)
            {
                var flag = *(uint*)(rawData + bodyOff + 16);

                if (flag == 0x04482c03)
                {
                    var round = *(rawData + bodyOff + 32) + 1;
                    var gateResult = *(rawData + bodyOff + 40);
                    var value = gateResult == 1 ? "gate-open" : "gate-fail";

                    log.Information($"[挖宝预测] 开门结果: {value} (第{round}轮) (bodyOff=0x{bodyOff:X2})");
                    if (configuration.EnableDebugLog)
                    {
                        log.Debug($"[诊断] 开门匹配 hex[0x00..0x50]: {DumpPacketHex(rawData)} | flag@0x{bodyOff + 16:X2}=0x{flag:X8} round@0x{bodyOff + 32:X2}={round - 1} result@0x{bodyOff + 40:X2}={gateResult}({value})");
                    }
                    predictionService.ProduceResult(value, null, round);
                    return;
                }
            }
        }
    }

    private static string DumpPacketHex(byte* data, int length = 80)
    {
        return Convert.ToHexString(new ReadOnlySpan<byte>(data, length));
    }

    /// <summary>
    /// ShowLogMessageUInt detour：拦截游戏 logmessage 调用，
    /// 当 logMessageID 为"打开了通往第{n}区的大门"时触发回调。
    /// value 参数即轮数（Case(1)→第二区→round=1，Case(2)→第三区→round=2，…）。
    /// </summary>
    private void OnShowLogMessageUInt(nint raptureLogModule, uint logMessageID, uint value)
    {
        showLogMessageUIntHook!.Original(raptureLogModule, logMessageID, value);

        try
        {
            if (!Constants.DoorOpenLogMessageIds.Contains(logMessageID)) return;

            var territoryID = CurrentTerritoryID;
            if (!Constants.DoorSelectionTerritoryIds.Contains(territoryID)) return;

            var round = (int)value;
            if (round < 1) return;

            log.Information($"[选门回退] ShowLogMessageUInt: logMsgId={logMessageID} value={value} → 开门(第{round}轮) terr={territoryID}");
            OnDoorGateOpenLogMessage?.Invoke(round);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "处理 ShowLogMessageUInt 时出错");
        }
    }
}
