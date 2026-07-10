namespace TreasureForecast.Utils;

public static class ResultFormatter
{
    public static string GetTreasureResultText(string value) => value switch
    {
        "wheel-low"     => "下级召唤",
        "wheel-medium"  => "中级召唤",
        "wheel-high"    => "上级召唤",
        "wheel-shift"   => "召唤式变动",
        "wheel-special" => "特殊召唤",
        "wheel-end"     => "失败",
        "wheel-open"    => "成功",
        "gate-open"     => "开门",
        "gate-fail"         => "失败",
        "dungeon-complete"  => "❀❀下底成功❀❀",
        "duty-wiped"        => "挖宝也能团灭？回家吧，孩子",
        _                   => ""
    };
}
