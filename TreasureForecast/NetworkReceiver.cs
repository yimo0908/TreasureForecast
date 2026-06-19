using Dalamud.Plugin.Services;
using System;
using System.Runtime.InteropServices;
using TreasureForecast.Models;

namespace TreasureForecast;

/// <summary>
/// 底层网络数据包接收器 —— Hook 游戏网络层来捕获所有 IPC 数据包。
/// 
/// Hook 策略：
/// 1. HandleActorControlPacket — 现有的 ActorControl Hook，用于巡梦金库 (category=407)
/// 2. OnReceivePacket — 通过 PacketDispatcher vtable 拦截所有 IPC 数据包
///    （包括转盘和开门等未知 opcode 的数据包，这些包 matcha 通过 ACT 原始数据获取）
/// </summary>
internal unsafe class NetworkReceiver : IDisposable
{
    private readonly IGameInteropProvider _gameInterop;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly TreasurePredictionService _predictionService;
    private readonly Configuration _configuration;

    // ============================================================
    // 委托定义
    // ============================================================

    /// <summary>
    /// HandleActorControlPacket 委托（匹配 FFXIVClientStructs 生成的签名）
    /// </summary>
    internal delegate void HandleActorControlPacketDelegate(
        uint entityId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetId, bool isRecorded);

    /// <summary>
    /// OnReceivePacket 委托（PacketDispatcher vtable 虚函数）
    /// 调用约定：x64 thiscall → RCX=this, RDX=targetId, R8=packetPtr
    /// </summary>
    private delegate void OnReceivePacketDelegate(nint dispatcher, uint targetId, nint packetPtr);

    // ============================================================
    // Hook 实例
    // ============================================================

    private Dalamud.Hooking.Hook<HandleActorControlPacketDelegate>? _actorControlHook;
    private Dalamud.Hooking.Hook<OnReceivePacketDelegate>? _onReceivePacketHook;

    private int _actorControlPacketCount;

    // ============================================================
    // IPC 数据包格式常量
    // ============================================================

    /// <summary>
    /// FFXIV 内部 IPC 头部大小（在 OnReceivePacket 层级）。
    /// IPC 帧结构：[2B size][2B opcode][12B unknown/ids] + payload
    /// 共 16 字节头部后跟负载数据。
    /// </summary>
    private const int IpcHeaderSize = 16;

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
        // 1. HandleActorControlPacket — 巡梦金库
        var actorControlAddr = ResolveActorControlAddress();
        if (actorControlAddr != nint.Zero)
        {
            _log.Information($"成功获取 HandleActorControlPacket 地址: 0x{actorControlAddr:X}");
            _actorControlHook = _gameInterop.HookFromAddress<HandleActorControlPacketDelegate>(
                actorControlAddr, OnActorControlPacket);
            _actorControlHook.Enable();
            _log.Information("HandleActorControlPacket Hook 已启用");
        }
        else
        {
            _log.Warning("无法解析 HandleActorControlPacket 地址！巡梦金库预测不可用。");
        }

        // 2. OnReceivePacket — 所有 IPC 数据包（转盘 + 开门）
        var receivePacketAddr = ResolveOnReceivePacketAddress();
        if (receivePacketAddr != nint.Zero)
        {
            _log.Information($"成功获取 OnReceivePacket 地址: 0x{receivePacketAddr:X}");
            _onReceivePacketHook = _gameInterop.HookFromAddress<OnReceivePacketDelegate>(
                receivePacketAddr, OnReceivePacket);
            _onReceivePacketHook.Enable();
            _log.Information("OnReceivePacket Hook 已启用 — 可捕获转盘和开门数据包");
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

    // ============================================================
    // 地址解析
    // ============================================================

    private static nint ResolveActorControlAddress()
    {
        var typeName = "FFXIVClientStructs.FFXIV.Client.Network.PacketDispatcher, FFXIVClientStructs";
        var pdType = Type.GetType(typeName);
        if (pdType == null) return nint.Zero;

        var addresses = pdType.GetNestedType("Addresses",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (addresses == null) return nint.Zero;

        var field = addresses.GetField("HandleActorControlPacket",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (field == null) return nint.Zero;

        return UnwrapAddress(field.GetValue(null));
    }

    private static nint ResolveOnReceivePacketAddress()
    {
        var typeName = "FFXIVClientStructs.FFXIV.Client.Network.PacketDispatcher, FFXIVClientStructs";
        var pdType = Type.GetType(typeName);
        if (pdType == null) return nint.Zero;

        var addresses = pdType.GetNestedType("Addresses",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (addresses == null) return nint.Zero;

        // FFXIVClientStructs 为 [VirtualTable] 结构生成 VirtualTable 或 StaticVirtualTable 字段
        var vtableField = addresses.GetField("VirtualTable",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? addresses.GetField("StaticVirtualTable",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (vtableField == null) return nint.Zero;

        var vtableAddr = UnwrapAddress(vtableField.GetValue(null));
        if (vtableAddr == nint.Zero) return nint.Zero;

        // OnReceivePacket 是 VirtualFunction(1) → vtable[1]
        return Marshal.ReadIntPtr(vtableAddr + IntPtr.Size);
    }

    private static nint UnwrapAddress(object? val)
    {
        if (val == null) return nint.Zero;
        if (val is nint ni) return ni;
        if (val is IntPtr ip) return ip;

        var t = val.GetType();
        try
        {
            var prop = t.GetProperty("Value");
            if (prop != null)
            {
                var inner = prop.GetValue(val);
                if (inner is nint ni2) return ni2;
                if (inner is IntPtr ip2) return ip2;
            }

            var field = t.GetField("Value",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var inner = field.GetValue(val);
                if (inner is nint ni3) return ni3;
                if (inner is IntPtr ip3) return ip3;
            }
        }
        catch { }

        return nint.Zero;
    }

    // ============================================================
    // HandleActorControlPacket Hook
    // ============================================================

    private void OnActorControlPacket(
        uint entityId, uint category,
        uint arg1, uint arg2, uint arg3, uint arg4,
        uint arg5, uint arg6, uint arg7, uint arg8,
        ulong targetId, bool isRecorded)
    {
        _actorControlHook!.Original(entityId, category, arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, targetId, isRecorded);

        try
        {
            var territoryId = (ushort)_clientState.TerritoryType;

            _actorControlPacketCount++;
            if (_actorControlPacketCount % 50 == 0 && category > 0)
            {
                _log.Info($"[诊断] ActorControl #{_actorControlPacketCount}: cat={category} a1={arg1} a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
            }

            // ---- 巡梦金库老虎机 (category = 407, 仅在 territory 1279) ----
            if (category == 407 && territoryId == 1279 && _configuration.EnableHypnoslot)
            {
                var result = (HypnoslotResultType)arg1;
                switch (result)
                {
                    case HypnoslotResultType.AllDiff:
                    case HypnoslotResultType.AllSame:
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

    // ============================================================
    // OnReceivePacket Hook — 所有 IPC 数据包
    // ============================================================

    private void OnReceivePacket(nint dispatcher, uint targetId, nint packetPtr)
    {
        _onReceivePacketHook!.Original(dispatcher, targetId, packetPtr);

        try
        {
            if (packetPtr == nint.Zero) return;

            // 读取 IPC 帧头部中的总大小（前 2 字节）
            // 如果格式不符，降级为无大小检查的匹配
            ushort totalFrameSize = *(ushort*)packetPtr;

            // 负载数据 = 跳过 IPC 头部后的数据
            // 负载数据格式与 matcha 中 GetRawData() 返回的相同
            byte* body = (byte*)(packetPtr + IpcHeaderSize);
            int bodySize = totalFrameSize > IpcHeaderSize ? totalFrameSize - IpcHeaderSize : 0;

            // ---- 转盘结果检查 (body=56 bytes) ----
            // matcha: DataLength==56, body[24]=level, body[40]=resultType
            if (_configuration.EnableWheelPrediction)
            {
                TryMatchWheelResult(body, bodySize);
            }

            // ---- 开门结果检查 (body=64 bytes) ----
            // matcha: DataLength==64, body[16]=flag, body[32]=round, body[40]=result
            if (_configuration.EnableGatePrediction)
            {
                TryMatchGateResult(body, bodySize);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "处理 OnReceivePacket 数据包时出错");
        }
    }

    /// <summary>
    /// 匹配宝物库转盘结果 (G10/G12/G15 转盘召唤)
    /// </summary>
    private void TryMatchWheelResult(byte* body, int bodySize)
    {
        if (bodySize != 0 && bodySize < 41)
            return;

        var level = *(uint*)(body + 24);

        string? source = level switch
        {
            7636061 => "G10 运河宝物库神殿",
            8508181 => "G12 梦羽宝殿",
            9413549 => "G15 育体宝殿",
            _ => null
        };

        if (source == null) return;

        // body[40] = TreasureShiftingWheelResultType
        string value = body[40] switch
        {
            191 => "wheel-low",
            192 => "wheel-medium",
            193 => "wheel-high",
            194 => "wheel-shift",
            195 => "wheel-special",
            196 => "wheel-end",
            _ => "unknown"
        };

        if (value != "unknown")
        {
            _log.Information($"[挖宝预测] 转盘结果: {source} → {value}");
            _predictionService.ProduceResult(value, source, 0);
        }
    }

    /// <summary>
    /// 匹配宝物库开门/路结果
    /// </summary>
    private void TryMatchGateResult(byte* body, int bodySize)
    {
        if (bodySize != 0 && bodySize < 41)
            return;

        // 特征标志 0x04482c03 位于 body[16]
        var flag = *(uint*)(body + 16);

        // 0x04482c03 的高位包含 0x04, 0x48, 0x2c, 0x03
        // 作为 uint 需要判断字节序
        if (flag == 0x04482c03)
        {
            var round = body[32] + 1;
            var value = body[40] == 1 ? "gate-open" : "gate-fail";

            _log.Information($"[挖宝预测] 开门结果: {value} (第{round}轮)");
            _predictionService.ProduceResult(value, "宝物库", round);
        }
    }
}
