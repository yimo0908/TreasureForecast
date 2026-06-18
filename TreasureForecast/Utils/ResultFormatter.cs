namespace TreasureForecast.Utils
{
    /// <summary>
    /// 结果格式化工具类 —— 移植自 matcha (抹茶 ACT 插件) 的结果格式化逻辑。
    /// 提供丰富的结果文本格式化功能，包括转盘结果、开门结果等。
    /// </summary>
    public static class ResultFormatter
    {
        /// <summary>
        /// 将结果值转换为中文描述
        /// 对应 matcha: Formatter.cs L152-L182 GetTreasureResultText()
        /// </summary>
        public static string GetTreasureResultText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            switch (value)
            {
                case "wheel-low":
                    return "下级召唤";
                case "wheel-medium":
                    return "中级召唤";
                case "wheel-high":
                    return "上级召唤";
                case "wheel-shift":
                    return "召唤式变动";
                case "wheel-special":
                    return "下级召唤";
                case "wheel-end":
                    return "召唤失败";
                case "wheel-open":
                    return "成功";
                case "gate-open":
                    return "开门";
                case "gate-fail":
                    return "失败";
            }

            return "";
        }

        /// <summary>
        /// 获取结果显示文本，包含来源和轮次信息
        /// </summary>
        public static string GetResultDisplayText(string value, string? source = null, int round = 0)
        {
            var resultText = GetTreasureResultText(value);
            if (string.IsNullOrEmpty(resultText))
            {
                return "";
            }

            var sourceText = string.IsNullOrEmpty(source) ? "挖宝预测" : source;
            var roundInfo = round > 0 ? $" (第{round}轮)" : "";

            return $"[{sourceText}] {resultText}{roundInfo}";
        }
    }
}
