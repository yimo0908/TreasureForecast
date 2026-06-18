namespace TreasureForecast.Models;

/// <summary>
/// 宝物库转盘结果类型（G10 运河宝物库神殿 / G12 梦羽宝殿 / G15 育体宝殿）
/// 对应 matcha 项目 TreasureShiftingWheelResultType.cs
/// </summary>
public enum ShiftingWheelResultType : byte
{
    Low = 191,    // 下级召唤
    Medium = 192, // 中级召唤
    High = 193,   // 上级召唤
    Shift = 194,  // 召唤式变动
    Special = 195,// 特殊召唤
    End = 196     // 召唤结束/失败
}

/// <summary>
/// 巡梦金库老虎机结果类型
/// 对应 matcha 项目 HypnoslotResultType.cs
/// </summary>
public enum HypnoslotResultType : byte
{
    AllDiff = 156,
    AllSame = 157,
    Preserve = 158,
    Reroll = 159,
    End = 160,
}
