using Dalamud.Configuration;
using System;

namespace TreasureForecast;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // ---- 挖宝预测开关 ----
    /// <summary>启用转盘结果预测（G10/G12/G15）</summary>
    public bool EnableWheelPrediction { get; set; } = true;
    
    /// <summary>启用开门结果预测</summary>
    public bool EnableGatePrediction { get; set; } = true;
    
    /// <summary>启用巡梦金库老虎机预测</summary>
    public bool EnableHypnoslot { get; set; } = true;

    // ---- 显示设置 ----
    /// <summary>发送结果到聊天框</summary>
    public bool ShowInChat { get; set; } = true;

    /// <summary>副本完成时提示下底成功</summary>
    public bool ShowDungeonCompleteMessage { get; set; } = true;

    /// <summary>游戏内Toast提示显示结果</summary>
    public bool ShowToastResult { get; set; } = true;
    
    /// <summary>最大历史记录条数</summary>
    public int MaxHistoryCount { get; set; } = 50;

    // ---- 成就追踪 ----
    /// <summary>启用成就进度追踪</summary>
    public bool EnableAchievementTracking { get; set; } = false;
    
    /// <summary>单项成就追踪开关</summary>
    public bool[] TrackedAchievements { get; set; } = new bool[10];

    // ---- 调试 ----
    /// <summary>启用 Debug 日志输出</summary>
    public bool EnableDebugLog { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
