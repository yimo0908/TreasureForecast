using System.Collections.Generic;
using System.Linq;

namespace TreasureForecast.Data;

public static class Constants
{
    public static readonly uint[] AchievementIDs =
    {
        1555u, 1951u, 1987u, 2139u, 2408u,
        2747u, 3019u, 3217u, 3556u, 3786u
    };

    public record TreasureTerritory(ushort ID, string Name);

    public static readonly TreasureTerritory[] TreasureTerritories =
    {
        new(588,  "G8 水城宝物库"),
        new(712,  "G10 运河宝物库"),
        new(725,  "深绿 运河宝物库深层"),
        new(794,  "G10 运河宝物库神殿"),
        new(879,  "G12 梦羽宝境"),
        new(924,  "G12 梦羽宝殿"),
        new(1000, "G14 惊奇百宝城"),
        new(1123, "G15 厄尔庇斯育体宝殿"),
        new(1209, "G17 加加财富天坑"),
        new(1279, "G18 巡梦金库"),
    };

    public static readonly HashSet<ushort> TerritoryIDSet = TreasureTerritories.Select(t => t.ID).ToHashSet();

    /// <summary>选门开门地图（有选门机制，但玩家不进动画时无预测网络包）</summary>
    public static readonly HashSet<ushort> DoorSelectionTerritoryIds = new() { 588, 712, 725, 879, 1000, 1123 };

    /// <summary>"打开了通往第{n}区的大门！" LogMessage ID（6区版 / 4区版）</summary>
    public static readonly HashSet<uint> DoorOpenLogMessageIds = new() { 6998u, 9365u };

    /// <summary>领地 ID → 名称 的 O(1) 查找表，替代每包 LINQ 线性扫描</summary>
    public static readonly Dictionary<ushort, string> TerritoryNameByID =
        TreasureTerritories.ToDictionary(t => t.ID, t => t.Name);
}
