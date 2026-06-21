using System.Collections.Generic;
using System.Linq;

namespace TreasureForecast.Data;

public static class Constants
{
    public static readonly uint[] AchievementIds =
    {
        1555u, 1951u, 1987u, 2139u, 2408u,
        2747u, 3019u, 3217u, 3556u, 3786u
    };

    public record TreasureTerritory(ushort Id, string Name);

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

    public static readonly HashSet<ushort> TerritoryIdSet = TreasureTerritories.Select(t => t.Id).ToHashSet();
}
