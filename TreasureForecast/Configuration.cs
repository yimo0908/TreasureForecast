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
    /// <summary>在聊天框显示结果</summary>
    public bool ShowInChat { get; set; } = true;
    
    /// <summary>在插件主窗口显示历史记录</summary>
    public bool ShowHistory { get; set; } = true;
    
    /// <summary>最大历史记录条数</summary>
    public int MaxHistoryCount { get; set; } = 50;

    /// <summary>设置窗口是否可移动</summary>
    public bool IsConfigWindowMovable { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
