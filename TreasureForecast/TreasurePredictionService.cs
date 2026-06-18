using System;
using System.Collections.Generic;
using TreasureForecast.Models;
using TreasureForecast.Utils;

namespace TreasureForecast;

/// <summary>
/// 挖宝预测服务 —— 移植自 matcha (抹茶 ACT 插件) 的挖宝预测逻辑。
/// 解析 FFXIV 网络数据包，预测宝物库转盘召唤结果、开门/路结果、以及藏宝点发现。
/// </summary>
public class TreasurePredictionService
{
    private readonly Queue<TreasureResultDTO> _recentResults = new();
    private const int MaxRecentResults = 50;

    /// <summary>
    /// 挖宝结果产生时触发
    /// </summary>
    public event Action<TreasureResultDTO>? OnTreasureResult;

    public IReadOnlyCollection<TreasureResultDTO> RecentResults => _recentResults.ToArray();

    /// <summary>
    /// 处理服务器发送的网络数据包，尝试匹配挖宝相关的数据包特征
    /// </summary>
    /// <param name="data">完整数据包字节</param>
    /// <param name="currentTerritoryId">当前区域 ID（用于巡梦金库判断）</param>
    public void ProcessServerPacket(byte[] data, ushort currentTerritoryId = 0)
    {
        if (data == null || data.Length == 0)
            return;

        // -------------------------------------------------------
        // 1. 宝物库转盘结果 (Treasure Shifting Wheel)
        //    数据包大小 = 56 (ActorControl 包大小)
        //    G10 运河宝物库神殿 = 0x007480FD → 7636061
        //    G12 梦羽宝殿      = 0x0081D995 → 8508181
        //    G15 育体宝殿      = 0x008F9A2D → 9413549
        //    对应 matcha: NetworkMonitor.cs L92-L141
        // -------------------------------------------------------
        if (data.Length == 56)
        {
            var level = BitConverter.ToUInt32(data, 24);
            string? source = level switch
            {
                7636061 => "G10 运河宝物库神殿",
                8508181 => "G12 梦羽宝殿",
                9413549 => "G15 育体宝殿",
                _ => null
            };

            if (source != null)
            {
                var resultType = (ShiftingWheelResultType)data[40];
                string value = resultType switch
                {
                    ShiftingWheelResultType.Low     => "wheel-low",
                    ShiftingWheelResultType.Medium  => "wheel-medium",
                    ShiftingWheelResultType.High    => "wheel-high",
                    ShiftingWheelResultType.Shift   => "wheel-shift",
                    ShiftingWheelResultType.Special => "wheel-special",
                    ShiftingWheelResultType.End     => "wheel-end",
                    _ => "unknown"
                };

                var dto = new TreasureResultDTO
                {
                    Value = value,
                    Source = source
                };
                AddResult(dto);
                OnTreasureResult?.Invoke(dto);
                return;
            }
        }

        // -------------------------------------------------------
        // 2. 宝物库开门/路结果 (Treasure Gate Result)
        //    数据包大小 = 72 (ActorControlSelf 包大小)
        //    特征标志 = 0x04482c03 (offset 16)
        //    offset 32 = 轮次, offset 40 = 1(开门成功)/其他(失败)
        //    对应 matcha: NetworkMonitor.cs L143-L155
        // -------------------------------------------------------
        if (data.Length == 72)
        {
            var flag = BitConverter.ToUInt32(data, 16);
            if (flag == 0x04482c03)
            {
                var round = data[32] + 1;
                var value = data[40] == 1 ? "gate-open" : "gate-fail";

                var dto = new TreasureResultDTO
                {
                    Value = value,
                    Round = round,
                    Source = "宝物库"
                };
                AddResult(dto);
                OnTreasureResult?.Invoke(dto);
                return;
            }
        }

        // -------------------------------------------------------
        // 3. ActorControl 类型数据包
        //    包括: 巡梦金库老虎机 (type=407)
        //    数据包大小 = 56
        //    对应 matcha: NetworkMonitor.cs L244-L301 (ActorControl handler)
        // -------------------------------------------------------
        if (data.Length == 56)
        {
            var type = BitConverter.ToUInt16(data, 0);

            // ---- 3a. 巡梦金库老虎机 (Hypnoslot - ActorControl type 407) ----
            // 需要限制区域 TerritoryType = 1279
            // 对应 matcha: NetworkMonitor.cs L265-L299
            if (type == 407 && currentTerritoryId == 1279)
            {
                var result = (HypnoslotResultType)data[4];
                switch (result)
                {
                    case HypnoslotResultType.AllDiff:
                    case HypnoslotResultType.AllSame:
                    case HypnoslotResultType.Reroll:
                        var openDto = new TreasureResultDTO
                        {
                            Value = "wheel-open",
                            Source = "巡梦金库"
                        };
                        AddResult(openDto);
                        OnTreasureResult?.Invoke(openDto);
                        break;
                    case HypnoslotResultType.End:
                        var endDto = new TreasureResultDTO
                        {
                            Value = "wheel-end",
                            Source = "巡梦金库"
                        };
                        AddResult(endDto);
                        OnTreasureResult?.Invoke(endDto);
                        break;
                }
            }
        }
    }

    private void AddResult(TreasureResultDTO dto)
    {
        _recentResults.Enqueue(dto);
        while (_recentResults.Count > MaxRecentResults)
            _recentResults.Dequeue();
    }

    /// <summary>
    /// 将结果值转换为中文描述
    /// 对应 matcha: Formatter.cs L152-L182 GetTreasureResultText()
    /// </summary>
    public static string GetResultText(string value)
    {
        return ResultFormatter.GetTreasureResultText(value) ?? value;
    }
}
