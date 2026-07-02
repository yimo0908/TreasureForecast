using Dalamud.Plugin.Services;
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using TreasureForecast.Data;

namespace TreasureForecast;

internal unsafe class NetworkReceiver : IDisposable
{
    private readonly IGameInteropProvider _gameInterop;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly TreasurePredictionService _predictionService;
    private readonly Configuration _configuration;

    internal delegate void HandleActorControlPacketDelegate(
        uint entityId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetId, bool isRecorded);

    private delegate void OnReceivePacketDelegate(nint dispatcher, uint targetId, nint packetPtr);

    private enum HypnoslotResultType : byte
    {
        AllDiff = 156,
        AllSame = 157,
        Preserve = 158,
        Reroll = 159,
        End = 160,
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

    private Dalamud.Hooking.Hook<HandleActorControlPacketDelegate>? _actorControlHook;
    private Dalamud.Hooking.Hook<OnReceivePacketDelegate>? _onReceivePacketHook;

    private int _actorControlPacketCount;

    private ushort _lastTerritoryId;
    private bool _isTreasureTerritory;

    private const int MinPacketSize = 0x20 + 40 + 1; // 73

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
        _gameInterop = gameInterop;
        _log = log;
        _clientState = clientState;
        _predictionService = predictionService;
        _configuration = configuration;
    }

    public void Initialize()
    {
        var actorControlAddr = ResolveActorControlAddress();
        if (actorControlAddr != nint.Zero)
        {
            _log.Information($"成功获取 HandleActorControlPacket 地址: 0x{actorControlAddr:X}");
            _actorControlHook = _gameInterop.HookFromAddress<HandleActorControlPacketDelegate>(
                actorControlAddr, OnActorControlPacket);
            _actorControlHook.Enable();
        }
        else
        {
            _log.Warning("无法解析 HandleActorControlPacket 地址！巡梦金库预测不可用。");
        }

        var receivePacketAddr = ResolveOnReceivePacketAddress();
        if (receivePacketAddr != nint.Zero)
        {
            _log.Information($"成功获取 OnReceivePacket 地址: 0x{receivePacketAddr:X}");
            _onReceivePacketHook = _gameInterop.HookFromAddress<OnReceivePacketDelegate>(
                receivePacketAddr, OnReceivePacket);
            _onReceivePacketHook.Enable();
        }
        else
        {
            _log.Warning("无法解析 OnReceivePacket 地址！G10/G12/G15 转盘和开门预测不可用。");
        }
    }

    public void Dispose()
    {
        _onReceivePacketHook?.Disable();
        _onReceivePacketHook?.Dispose();
        _actorControlHook?.Disable();
        _actorControlHook?.Dispose();
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
        return Marshal.ReadIntPtr(vtableAddr + IntPtr.Size);
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
        if (_predictionService.HasCurrentMapName) return;
        var territoryId = (ushort)_clientState.TerritoryType;

        if (territoryId == _lastTerritoryId && !_isTreasureTerritory) return;
        _lastTerritoryId = territoryId;

        if (Constants.TerritoryNameById.TryGetValue(territoryId, out var name))
        {
            _isTreasureTerritory = true;
            _predictionService.SetCurrentMapName(name);
        }
        else
        {
            _isTreasureTerritory = false;
        }
    }

    private void OnActorControlPacket(
        uint entityId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetId, bool isRecorded)
    {
        _actorControlHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);

        try
        {
            TrySetCurrentMapName();

            var territoryId = (ushort)_clientState.TerritoryType;

            _actorControlPacketCount++;
            if (_configuration.EnableDebugLog && _isTreasureTerritory && _actorControlPacketCount % 50 == 0 && category > 0)
            {
                _log.Debug($"[诊断] ActorControl #{_actorControlPacketCount}: cat={category} a1={arg1} a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
            }

            // 巡梦金库老虎机 (category = 407, 仅在 territory 1279)
            if (category == 407 && territoryId == 1279 && _configuration.EnableHypnoslot)
            {
                if (_configuration.EnableDebugLog)
                {
                    var enumName = Enum.IsDefined(typeof(HypnoslotResultType), (byte)arg1)
                        ? ((HypnoslotResultType)(byte)arg1).ToString()
                        : "Unknown";
                    _log.Information($"[Hypnoslot] arg1={arg1} ({enumName}) a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
                }

                var result = (HypnoslotResultType)arg1;
                switch (result)
                {
                    case HypnoslotResultType.AllDiff:
                    case HypnoslotResultType.AllSame:
                    case HypnoslotResultType.Preserve:
                    case HypnoslotResultType.Reroll:
                        _predictionService.ProduceResult("wheel-open", "巡梦金库", 0);
                        break;
                    case HypnoslotResultType.End:
                        _predictionService.ProduceResult("wheel-end", "巡梦金库", 0);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "处理 ActorControl 数据包时出错");
        }
    }

    private void OnReceivePacket(nint dispatcher, uint targetId, nint packetPtr)
    {
        _onReceivePacketHook!.Original(dispatcher, targetId, packetPtr);

        try
        {
            TrySetCurrentMapName();

            if (packetPtr == nint.Zero) return;

            TryMatchPacket((byte*)packetPtr);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "处理 OnReceivePacket 数据包时出错");
        }
    }

    private void TryMatchPacket(byte* rawData)
    {
        var checkWheel = _configuration.EnableWheelPrediction;
        var checkGate = _configuration.EnableGatePrediction;
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
                        _log.Information($"[挖宝预测] 转盘结果: {source} → {value} (bodyOff=0x{bodyOff:X2})");
                        if (_configuration.EnableDebugLog)
                        {
                            _log.Debug($"[诊断] 转盘匹配 hex[0x00..0x50]: {DumpPacketHex(rawData)} | level@0x{bodyOff + 24:X2}={level} result@0x{bodyOff + 40:X2}=0x{resultByte:X2}({value})");
                        }
                        _predictionService.ProduceResult(value, source, 0);
                        return;
                    }

                    _log.Warning($"[诊断] 转盘近似匹配: level={level}({source}) bodyOff=0x{bodyOff:X2} 但 resultByte=0x{resultByte:X2} 未知，hex[0x00..0x50]: {DumpPacketHex(rawData)}");
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

                    _log.Information($"[挖宝预测] 开门结果: {value} (第{round}轮) (bodyOff=0x{bodyOff:X2})");
                    if (_configuration.EnableDebugLog)
                    {
                        _log.Debug($"[诊断] 开门匹配 hex[0x00..0x50]: {DumpPacketHex(rawData)} | flag@0x{bodyOff + 16:X2}=0x{flag:X8} round@0x{bodyOff + 32:X2}={round - 1} result@0x{bodyOff + 40:X2}={gateResult}({value})");
                    }
                    _predictionService.ProduceResult(value, null, round);
                    return;
                }
            }
        }
    }

    private static string DumpPacketHex(byte* data, int length = 80)
    {
        return Convert.ToHexString(new ReadOnlySpan<byte>(data, length));
    }
}
