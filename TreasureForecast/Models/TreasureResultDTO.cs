namespace TreasureForecast.Models;

/// <summary>
/// 挖宝结果 DTO
/// 对应 matcha 项目 TreasureResultDTO.cs
/// </summary>
public class TreasureResultDTO
{
    /// <summary>
    /// 结果字符串，格式：
    /// - wheel-low / wheel-medium / wheel-high / wheel-shift / wheel-special / wheel-end（转盘召唤）
    /// - wheel-open（转盘成功继续）
    /// - gate-open（开门成功）/ gate-fail（开门失败）
    /// </summary>
    public string Value { get; init; } = string.Empty;
    
    /// <summary>轮次（开门时使用）</summary>
    public int Round { get; init; }
    
    /// <summary>所属宝物库名称（G10/G12/G15/巡梦金库/普通）</summary>
    public string? Source { get; init; }
}
