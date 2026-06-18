using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TreasureForecast.Models;

namespace TreasureForecast.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly List<TreasureResultDTO> _results = new();

    public MainWindow(Plugin plugin)
        : base("挖宝预测##TreasureForecastMain", ImGuiWindowFlags.NoScrollbar)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _plugin = plugin;
    }

    public void Dispose() { }

    public void AddResult(TreasureResultDTO dto)
    {
        _results.Add(dto);

        // 限制历史数量
        var maxHistory = _plugin.Configuration.MaxHistoryCount;
        while (_results.Count > maxHistory)
        {
            _results.RemoveAt(0);
        }
    }

    public override void Draw()
    {
        if (!_plugin.Configuration.ShowHistory)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "历史记录已关闭");
            ImGui.Text("请在设置中启用「显示历史记录」");
            return;
        }

        DrawHistory();
    }

    private void DrawHistory()
    {
        ImGui.Text("=== 历史记录 ===");
        ImGuiHelpers.ScaledDummy(4);

        using var child = ImRaii.Child("##historyList", Vector2.Zero, true);
        if (!child.Success) return;

        // 倒序显示（最新的在上面）
        for (int i = _results.Count - 1; i >= 0; i--)
        {
            var dto = _results[i];
            var text = TreasurePredictionService.GetResultText(dto.Value);
            var source = dto.Source ?? "";
            var roundInfo = dto.Round > 0 ? $" (第{dto.Round}轮)" : "";

            var color = dto.Value switch
            {
                "wheel-low"     => new Vector4(0.6f, 0.6f, 1.0f, 1),    // 蓝色 - 下级
                "wheel-medium"  => new Vector4(0.4f, 1.0f, 0.6f, 1),    // 绿色 - 中级
                "wheel-high"    => new Vector4(1.0f, 0.8f, 0.2f, 1),    // 金色 - 上级
                "wheel-shift"   => new Vector4(1.0f, 0.4f, 0.8f, 1),    // 粉紫 - 变动
                "wheel-special" => new Vector4(0.8f, 0.4f, 1.0f, 1),    // 紫色 - 特殊
                "wheel-end"     => new Vector4(1.0f, 0.3f, 0.3f, 1),    // 红色 - 失败
                "wheel-open"    => new Vector4(0.3f, 1.0f, 0.3f, 1),    // 亮绿 - 开门
                "gate-open"     => new Vector4(0.3f, 1.0f, 0.3f, 1),    // 亮绿 - 开门成功
                "gate-fail"     => new Vector4(1.0f, 0.3f, 0.3f, 1),    // 红色 - 开门失败
                _               => new Vector4(1, 1, 1, 1)
            };

            var prefix = source.Length > 0 ? $"[{source}] " : "";
            ImGui.TextColored(color, $"{prefix}{text}{roundInfo}");
        }
    }
}
