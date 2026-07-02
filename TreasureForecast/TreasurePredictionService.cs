using System;
using System.Collections.Generic;
using System.Linq;
using TreasureForecast.Models;

namespace TreasureForecast;

public class TreasurePredictionService
{
    private string? _currentMapName;

    public bool HasCurrentMapName => _currentMapName != null;

    public void SetCurrentMapName(string name) => _currentMapName = name;
    public void ClearCurrentMapName() => _currentMapName = null;

    public event Action<TreasureResultDTO>? OnTreasureResult;

    private const long DedupWindowMs = 5000;
    private readonly Dictionary<string, long> _recentResultTimestamps = new();
    private readonly object _dedupLock = new();
    private int _dedupCleanupCounter;
    private const int DedupCleanupInterval = 64;

    /// <summary>
    /// 产生一个挖宝结果，同一 (Value, Source, Round) 在 DedupWindow 内只输出一次。
    /// </summary>
    public void ProduceResult(string value, string? source, int round = 0)
    {
        var key = $"{value}|{source ?? ""}|{round}";
        var now = Environment.TickCount64;

        lock (_dedupLock)
        {
            if (_recentResultTimestamps.TryGetValue(key, out var lastTime) && now - lastTime < DedupWindowMs)
                return;

            _recentResultTimestamps[key] = now;

            if (++_dedupCleanupCounter >= DedupCleanupInterval)
            {
                _dedupCleanupCounter = 0;
                var threshold = now - DedupWindowMs;
                var expired = _recentResultTimestamps
                    .Where(kvp => kvp.Value < threshold)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var k in expired)
                    _recentResultTimestamps.Remove(k);
            }
        }

        var actualSource = _currentMapName ?? source;

        if (value == "wheel-end" || value == "dungeon-complete")
            _currentMapName = null;

        OnTreasureResult?.Invoke(new TreasureResultDTO
        {
            Value = value,
            Source = actualSource,
            Round = round
        });
    }
}
