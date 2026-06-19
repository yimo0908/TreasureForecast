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
    /// OnReceivePacket 的 nint packetPtr 数据格式在游戏各版本中不一致。
    /// 可能指向：纯负载数据（无 IPC 头）、16B 头 + 负载、或 32B 头 + 负载。
    /// 因此我们不假定头部大小，而是直接从 packetPtr 按多个候选偏移量试探匹配。
    /// 
    /// matcha 对转盘结果的偏移基准（body[24]=level, body[40]=resultType）：
    ///   - 候选偏移 0x00：packetPtr 指向纯负载 → level@{24}, result@{40}
    ///   - 候选偏移 0x10：packetPtr 指向 16B IPC 头 → level@{40}, result@{56}
    ///   - 候选偏移 0x20：packetPtr 指向 32B ACT 类头 → level@{56}, result@{72}
    /// 开门结果的偏移基准（body[16]=flag, body[32]=round, body[40]=result）同理。
    /// 
    /// 由于 level 值（7636061/8508181/9413549）和 flag（0x04482c03）非常特异，
    /// 误匹配概率极低。
    /// </summary>
    private static readonly int[] CandidateBodyOffsets = { 0x00, 0x10, 0x20 };

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
            if (_configuration.EnableDebugLog && _actorControlPacketCount % 50 == 0 && category > 0)
            {
                _log.Debug($"[诊断] ActorControl #{_actorControlPacketCount}: cat={category} a1={arg1} a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
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

    private int _receivePacketCount;

    private void OnReceivePacket(nint dispatcher, uint targetId, nint packetPtr)
    {
        _onReceivePacketHook!.Original(dispatcher, targetId, packetPtr);

        try
        {
            if (packetPtr == nint.Zero) return;

            _receivePacketCount++;

            // 诊断：每 200 包输出一次前 48 字节的 hex dump（仅在 Debug 模式开启时）
            if (_configuration.EnableDebugLog && _receivePacketCount % 200 == 0)
            {
                var span = new ReadOnlySpan<byte>((void*)packetPtr, 48);
                var hex = BitConverter.ToString(span.ToArray()).Replace("-", " ");
                _log.Debug($"[诊断] OnReceivePacket #{_receivePacketCount}: {hex}");
            }

            // 不假定 IPC 帧头部格式，改用多个候选偏移量试探
            if (_configuration.EnableWheelPrediction)
            {
                TryMatchWheelResult((byte*)packetPtr);
            }

            if (_configuration.EnableGatePrediction)
            {
                TryMatchGateResult((byte*)packetPtr);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "处理 OnReceivePacket 数据包时出错");
        }
    }

    /// <summary>
    /// 尝试从 rawData 中匹配宝物库转盘结果 (G10/G12/G15)
    /// 在多个候选 body 偏移量上逐一试探 matcha 的 level+result 签名。
    /// </summary>
    private void TryMatchWheelResult(byte* rawData)
    {
        foreach (var bodyOff in CandidateBodyOffsets)
        {
            // matcha: body[24] = level (uint), body[40] = resultType (byte)
            var level = *(uint*)(rawData + bodyOff + 24);

            string? source = level switch
            {
                7636061 => "G10 运河宝物库神殿",
                8508181 => "G12 梦羽宝殿",
                9413549 => "G15 育体宝殿",
                _ => null
            };

            if (source == null) continue;

            byte resultByte = *(rawData + bodyOff + 40);
            string value = resultByte switch
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
                _log.Information($"[挖宝预测] 转盘结果: {source} → {value} (bodyOff=0x{bodyOff:X2})");
                _predictionService.ProduceResult(value, source, 0);
                return;
            }

            // level 匹配但 resultByte 异常 — 可能 bodyOff 错误，继续尝试其他偏移
        }
    }

    /// <summary>
    /// 尝试从 rawData 中匹配宝物库开门/路结果
    /// </summary>
    private void TryMatchGateResult(byte* rawData)
    {
        foreach (var bodyOff in CandidateBodyOffsets)
        {
            // matcha: body[16] = flag (0x04482c03)
            var flag = *(uint*)(rawData + bodyOff + 16);

            if (flag != 0x04482c03) continue;

            var round = *(rawData + bodyOff + 32) + 1;
            var value = *(rawData + bodyOff + 40) == 1 ? "gate-open" : "gate-fail";

            _log.Information($"[挖宝预测] 开门结果: {value} (第{round}轮) (bodyOff=0x{bodyOff:X2})");
            _predictionService.ProduceResult(value, "宝物库", round);
            return;
        }
    }
}
