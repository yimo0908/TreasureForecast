using System;
using System.Collections.Generic;
using System.Linq;
using TreasureForecast.Models;

namespace TreasureForecast;

public class TreasurePredictionService
{
    private string? currentMapName;

    public bool HasCurrentMapName => currentMapName != null;

    public void SetCurrentMapName(string name) => currentMapName = name;
    public void ClearCurrentMapName() => currentMapName = null;

    public event Action<TreasureResultDTO>? OnTreasureResult;

    private const long DedupWindowMs = 5000;
    private readonly Dictionary<string, long> recentResultTimestamps = new();
    private readonly object dedupLock = new();
    private int dedupCleanupCounter;
    private const int DedupCleanupInterval = 64;

    /// <summary>
    /// 产生一个挖宝结果，同一 (Value, Source, Round) 在 DedupWindow 内只输出一次。
    /// </summary>
    public void ProduceResult(string value, string? source, int round = 0)
    {
        var key = $"{value}|{source ?? ""}|{round}";
        var now = Environment.TickCount64;

        lock (dedupLock)
        {
            if (recentResultTimestamps.TryGetValue(key, out var lastTime) && now - lastTime < DedupWindowMs)
                return;

            recentResultTimestamps[key] = now;

            if (++dedupCleanupCounter >= DedupCleanupInterval)
            {
                dedupCleanupCounter = 0;
                var threshold = now - DedupWindowMs;
                var expired = recentResultTimestamps
                    .Where(kvp => kvp.Value < threshold)
                    .Select(kvp => kvp.Key)
                    .ToList();
                foreach (var k in expired)
                    recentResultTimestamps.Remove(k);
            }
        }

        var actualSource = currentMapName ?? source;

        if (value == "wheel-end" || value == "dungeon-complete")
            currentMapName = null;

        OnTreasureResult?.Invoke(new TreasureResultDTO
        {
            Value = value,
            Source = actualSource,
            Round = round
        });
    }
}
