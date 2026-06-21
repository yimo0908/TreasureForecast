using System;

namespace TreasureForecast.Models;

public class AchievementProgressInfo
{
    public uint AchievementId { get; init; }
    public string AchievementName { get; set; } = "";
    public string TitleName { get; set; } = "";

    public uint Current { get; set; }
    public uint Max { get; set; }

    public float Ratio => Max > 0 ? Math.Clamp((float)Current / Max, 0f, 1f) : 0f;
    public bool IsComplete => Max > 0 && Current >= Max;
}
