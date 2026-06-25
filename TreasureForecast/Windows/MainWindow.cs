using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TreasureForecast.Models;
using TreasureForecast.Utils;

namespace TreasureForecast.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly List<TreasureResultDTO> _results = new();
    private bool _isInsideTreasureDungeon;

    public MainWindow(Plugin plugin)
        : base("挖宝预测##TreasureForecastMain", ImGuiWindowFlags.NoScrollbar)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        _plugin = plugin;

        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new(1),
            Click = _ => plugin.ToggleConfigUi()
        });
    }

    public void Dispose() { }

    public void AddDutyCompleteSeparator()
    {
        _isInsideTreasureDungeon = false;
        _results.Add(new TreasureResultDTO { Value = "dungeon-complete", Timestamp = DateTime.Now });
        _results.Add(new TreasureResultDTO { Value = "separator", Timestamp = DateTime.Now });
    }

    public void AddResult(TreasureResultDTO dto)
    {
        // 检测新进挖宝图：首次收到非结束的转盘结果时插入分割线
        if (dto.Value.StartsWith("wheel-") && dto.Value != "wheel-end" && !_isInsideTreasureDungeon)
        {
            _isInsideTreasureDungeon = true;

            // 避免与 AddDutyCompleteSeparator 已插入的分割线重复
            if (!(_results.Count > 0 && _results[^1].Value == "separator"))
                _results.Add(new TreasureResultDTO { Value = "separator", Timestamp = dto.Timestamp });
        }

        _results.Add(dto);

        // 检测挖宝结束
        if (dto.Value == "wheel-end")
        {
            _isInsideTreasureDungeon = false;
        }

        // 限制历史数量（分割线不计入限制）
        var maxHistory = _plugin.Configuration.MaxHistoryCount;
        var nonSeparatorCount = _results.Count(r => r.Value != "separator");
        while (nonSeparatorCount > maxHistory)
        {
            var idx = _results.FindIndex(r => r.Value != "separator");
            if (idx >= 0)
            {
                _results.RemoveAt(idx);
                nonSeparatorCount--;
            }
            else break;
        }
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("##mainTabs"))
        {
            if (ImGui.BeginTabItem("历史记录"))
            {
                DrawHistory();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("成就进度"))
            {
                DrawAchievementProgress();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawHistory()
    {
        ImGui.Text("=== 历史记录 ===");
        ImGui.SameLine();

        // Clear 按钮
        if (ImGui.SmallButton("清空"))
        {
            _results.Clear();
            _isInsideTreasureDungeon = false;
        }
        ImGuiHelpers.ScaledDummy(4);

        using var child = ImRaii.Child("##historyList", Vector2.Zero, true);
        if (!child.Success) return;

        // 倒序显示（最新的在上面）
        for (int i = _results.Count - 1; i >= 0; i--)
        {
            var dto = _results[i];

            // 分割线
            if (dto.Value == "separator")
            {
                ImGuiHelpers.ScaledDummy(2);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(2);
                continue;
            }

            var text = dto.Value == "dungeon-complete"
                ? "❀❀下底成功❀❀"
                : ResultFormatter.GetTreasureResultText(dto.Value);
            var roundInfo = dto.Round > 0 ? $" (第{dto.Round}轮)" : "";

            var color = dto.Value switch
            {
                "wheel-low"       => new Vector4(0.6f, 0.6f, 1.0f, 1),    // 蓝色 - 下级
                "wheel-medium"    => new Vector4(0.4f, 1.0f, 0.6f, 1),    // 绿色 - 中级
                "wheel-high"      => new Vector4(1.0f, 0.3f, 0.3f, 1),    // 红色 - 上级
                "wheel-shift"     => new Vector4(1.0f, 0.8f, 0.2f, 1),    // 金色 - 变动
                "wheel-special"   => new Vector4(0.75f, 0.75f, 0.8f, 1),  // 银色 - 特殊
                "wheel-end"       => new Vector4(0.8f, 0.4f, 1.0f, 1),    // 紫色 - 失败
                "wheel-open"      => new Vector4(0.3f, 1.0f, 0.3f, 1),    // 亮绿 - 开门
                "gate-open"       => new Vector4(0.3f, 1.0f, 0.3f, 1),    // 亮绿 - 开门成功
                "gate-fail"       => new Vector4(1.0f, 0.3f, 0.3f, 1),    // 红色 - 开门失败
                "dungeon-complete" => new Vector4(1f, 0.8f, 0.2f, 1),     // 金色 - 下底成功
                _                 => new Vector4(1, 1, 1, 1)
            };

            var time = dto.Timestamp.ToString("HH:mm:ss");
            var sourcePrefix = string.IsNullOrEmpty(dto.Source) ? "" : $"[{dto.Source}] ";
            ImGui.TextColored(color, $"[{time}] {sourcePrefix}{text}{roundInfo}");
        }
    }

    private void DrawAchievementProgress()
    {
        var achList = _plugin.Achievements;
        if (achList == null || achList.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "成就数据未就绪");
            return;
        }

        var cfg = _plugin.Configuration;
        IEnumerable<AchievementProgressInfo> displayList = achList;
        if (cfg.EnableAchievementTracking)
        {
            displayList = achList.Where((a, i) => i < cfg.TrackedAchievements.Length && cfg.TrackedAchievements[i]).ToList();
            if (!displayList.Any())
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "请在设置中选择要追踪的成就");
                return;
            }
        }

        ImGui.BeginChild("##achievementList", Vector2.Zero, true);
        var barWidth = ImGui.GetContentRegionAvail().X;
        var first = true;

        foreach (var ach in displayList)
        {
            if (!first)
            {
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(4);
            }
            first = false;

            ImGui.Text(ach.AchievementName);

            if (ach.Max > 0)
            {
                var ratio = ach.Ratio;

                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, GetProgressColor(ratio));
                var progressWidth = ach.TitleName.Length > 0 ? barWidth * 0.65f : barWidth;
                ImGui.ProgressBar(ratio, new Vector2(progressWidth, 22f),
                    $"{ach.Current} / {ach.Max} ({ratio * 100:F0}%)");
                ImGui.PopStyleColor();

                if (ach.TitleName.Length > 0)
                {
                    ImGui.SameLine();
                    var titleColor = ach.IsComplete
                        ? new Vector4(0.2f, 1f, 0.2f, 1f)
                        : new Vector4(0.5f, 0.5f, 0.5f, 1f);
                    ImGui.TextColored(titleColor, ach.TitleName);
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "正在获取数据...");
            }

            ImGuiHelpers.ScaledDummy(4);
        }

        ImGui.EndChild();

        if (ImGui.Button("导出"))
        {
            _plugin.ExportAchievementProgress();
        }
    }

    /// <summary>根据进度返回对应颜色：红(&lt;25%)→黄(&lt;50%)→蓝(&lt;100%)→绿(=100%)。</summary>
    private static readonly uint ProgressColorComplete = ImGui.GetColorU32(new Vector4(0.2f,  1f,   0.2f,  1f));
    private static readonly uint ProgressColorHigh    = ImGui.GetColorU32(new Vector4(0.3f,  0.7f,  1f,   1f));
    private static readonly uint ProgressColorMid     = ImGui.GetColorU32(new Vector4(1f,   0.85f, 0.2f,  1f));
    private static readonly uint ProgressColorLow     = ImGui.GetColorU32(new Vector4(1f,   0.3f,  0.3f,  1f));

    private static uint GetProgressColor(float progress) => progress switch
    {
        >= 1f    => ProgressColorComplete,
        >= 0.5f  => ProgressColorHigh,
        >= 0.25f => ProgressColorMid,
        _        => ProgressColorLow,
    };
}
