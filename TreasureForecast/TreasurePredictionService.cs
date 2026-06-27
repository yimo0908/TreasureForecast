using System;
using System.Collections.Generic;
using TreasureForecast.Models;

namespace TreasureForecast;

/// <summary>
/// 挖宝预测服务 —— 移植自 matcha (抹茶 ACT 插件) 的挖宝预测逻辑。
/// 解析 FFXIV 网络数据包，预测宝物库转盘召唤结果、开门/路结果、以及藏宝点发现。
/// </summary>
public class TreasurePredictionService
{
    private string? _currentMapName;

    public bool HasCurrentMapName => _currentMapName != null;

    public void SetCurrentMapName(string name)
    {
        _currentMapName = name;
    }

    public void ClearCurrentMapName()
    {
        _currentMapName = null;
    }

    /// <summary>
    /// 挖宝结果产生时触发
    /// </summary>
    public event Action<TreasureResultDTO>? OnTreasureResult;

    // ---- 去重 ----

    /// <summary>去重窗口（同一结果在此时间内只输出一次），单位毫秒</summary>
    private const long DedupWindowMs = 5000;

    /// <summary>最近产生的结果快照，用于去重</summary>
    private readonly Dictionary<string, long> _recentResultTimestamps = new();

    private readonly object _dedupLock = new();

    /// <summary>写入计数器，达到阈值时触发过期清理</summary>
    private int _dedupCleanupCounter;
    private const int DedupCleanupInterval = 64;

    /// <summary>
    /// 产生一个挖宝结果（统一入口，由 NetworkReceiver 调用）
    /// 自动去重：同一 (Value, Source, Round) 在 DedupWindow 内只输出一次。
    /// </summary>
    public void ProduceResult(string value, string? source, int round = 0)
    {
        // 构造去重 key
        var key = $"{value}|{source ?? ""}|{round}";
        var now = Environment.TickCount64;

        lock (_dedupLock)
        {
            if (_recentResultTimestamps.TryGetValue(key, out var lastTime))
            {
                if (now - lastTime < DedupWindowMs)
                {
                    // 同一结果在去重窗口内 → 跳过
                    return;
                }
            }

            _recentResultTimestamps[key] = now;

            // 定期清理过期条目，防止字典无限增长
            if (++_dedupCleanupCounter >= DedupCleanupInterval)
            {
                _dedupCleanupCounter = 0;
                var threshold = now - DedupWindowMs;
                List<string>? expiredKeys = null;
                foreach (var kvp in _recentResultTimestamps)
                {
                    if (kvp.Value < threshold)
                    {
                        expiredKeys ??= new List<string>();
                        expiredKeys.Add(kvp.Key);
                    }
                }
                if (expiredKeys != null)
                {
                    foreach (var k in expiredKeys)
                        _recentResultTimestamps.Remove(k);
                }
            }
        }

        var actualSource = _currentMapName ?? source;

        if (value == "wheel-end" || value == "dungeon-complete")
            _currentMapName = null;

        var dto = new TreasureResultDTO
        {
            Value = value,
            Source = actualSource,
            Round = round
        };

        OnTreasureResult?.Invoke(dto);
    }
}
