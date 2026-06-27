using Dalamud.Plugin.Services;
using System;
using System.Runtime.InteropServices;
using TreasureForecast.Data;

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
    // 嵌套枚举
    // ============================================================

    /// <summary>
    /// 巡梦金库老虎机结果类型
    /// 对应 matcha 项目 HypnoslotResultType.cs
    /// </summary>
    private enum HypnoslotResultType : byte
    {
        AllDiff = 156,
        AllSame = 157,
        Preserve = 158,
        Reroll = 159,
        End = 160,
    }

    /// <summary>
    /// 宝物库转盘结果类型（G10 运河宝物库神殿 / G12 梦羽宝殿 / G15 育体宝殿）
    /// </summary>
    private enum ShiftingWheelResultType : byte
    {
        Low = 191,
        Medium = 192,
        High = 193,
        Shift = 194,
        Special = 195,
        End = 196,
    }

    // ============================================================
    // Hook 实例
    // ============================================================

    private Dalamud.Hooking.Hook<HandleActorControlPacketDelegate>? _actorControlHook;
    private Dalamud.Hooking.Hook<OnReceivePacketDelegate>? _onReceivePacketHook;

    private int _actorControlPacketCount;

    // ============================================================
    // 地图名缓存（避免每包 LINQ 线性扫描）
    // ============================================================

    private ushort _lastTerritoryId;
    private bool _isTreasureTerritory;

    /// <summary>
    /// 匹配所需的最小数据包长度（bodyOff=0x20 时读取 offset 40 → 字节 72）。
    /// FFXIV IPC 数据包均大于此值；若包过短，OnReceivePacket 的 try/catch 会兜底。
    /// </summary>
    private const int MinPacketSize = 0x20 + 40 + 1; // 73

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
    // 地图名缓存辅助（O(1) 查找 + 领地缓存，避免每包 LINQ 扫描）
    // ============================================================

    private void TrySetCurrentMapName()
    {
        if (_predictionService.HasCurrentMapName) return;
        var territoryId = (ushort)_clientState.TerritoryType;

        // 缓存上次领地：若已知非宝藏领地则跳过，避免每包重复查表
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
            TrySetCurrentMapName();

            var territoryId = (ushort)_clientState.TerritoryType;

            _actorControlPacketCount++;
            if (_configuration.EnableDebugLog && _actorControlPacketCount % 50 == 0 && category > 0)
            {
                _log.Debug($"[诊断] ActorControl #{_actorControlPacketCount}: cat={category} a1={arg1} a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
            }

            // ---- 巡梦金库老虎机 无过滤日志 (category = 407) ----
            if (_configuration.EnableDebugLog && category == 407)
            {
                var enumName = Enum.IsDefined(typeof(HypnoslotResultType), (byte)arg1)
                    ? ((HypnoslotResultType)(byte)arg1).ToString()
                    : "Unknown";
                _log.Information($"[Hypnoslot] arg1={arg1} ({enumName}) a2={arg2} a3={arg3} a4={arg4} terr={territoryId}");
            }

            // ---- 巡梦金库老虎机 (category = 407, 仅在 territory 1279) ----
            if (category == 407 && territoryId == 1279 && _configuration.EnableHypnoslot)
            {
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

    // ============================================================
    // OnReceivePacket Hook — 所有 IPC 数据包
    // ============================================================

    private int _receivePacketCount;

    private void OnReceivePacket(nint dispatcher, uint targetId, nint packetPtr)
    {
        _onReceivePacketHook!.Original(dispatcher, targetId, packetPtr);

        try
        {
            TrySetCurrentMapName();

            if (packetPtr == nint.Zero) return;

            _receivePacketCount++;

            // 诊断：每 200 包输出一次前 48 字节的 hex dump（仅在 Debug 模式开启时）
            if (_configuration.EnableDebugLog && _receivePacketCount % 200 == 0)
            {
                var span = new ReadOnlySpan<byte>((void*)packetPtr, 48);
                var hex = Convert.ToHexString(span);
                _log.Debug($"[诊断] OnReceivePacket #{_receivePacketCount}: {hex}");
            }

            // 合并转盘+开门匹配，单次遍历候选偏移量
            TryMatchPacket((byte*)packetPtr);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "处理 OnReceivePacket 数据包时出错");
        }
    }

    /// <summary>
    /// 合并匹配转盘和开门结果，单次遍历候选偏移量。
    /// 转盘签名（level@24）和开门签名（flag@16）互斥，首个匹配即返回。
    /// </summary>
    private void TryMatchPacket(byte* rawData)
    {
        var checkWheel = _configuration.EnableWheelPrediction;
        var checkGate = _configuration.EnableGatePrediction;
        if (!checkWheel && !checkGate) return;

        foreach (var bodyOff in CandidateBodyOffsets)
        {
            // ---- 转盘匹配: body[24] = level (uint), body[40] = resultType (byte) ----
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
                    string value = result switch
                    {
                        ShiftingWheelResultType.Low => "wheel-low",
                        ShiftingWheelResultType.Medium => "wheel-medium",
                        ShiftingWheelResultType.High => "wheel-high",
                        ShiftingWheelResultType.Shift => "wheel-shift",
                        ShiftingWheelResultType.Special => "wheel-special",
                        ShiftingWheelResultType.End => "wheel-end",
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

            // ---- 开门匹配: body[16] = flag (0x04482c03), body[32] = round, body[40] = result ----
            if (checkGate)
            {
                var flag = *(uint*)(rawData + bodyOff + 16);

                if (flag == 0x04482c03)
                {
                    var round = *(rawData + bodyOff + 32) + 1;
                    var value = *(rawData + bodyOff + 40) == 1 ? "gate-open" : "gate-fail";

                    _log.Information($"[挖宝预测] 开门结果: {value} (第{round}轮) (bodyOff=0x{bodyOff:X2})");
                    _predictionService.ProduceResult(value, null, round);
                    return;
                }
            }
        }
    }
}
