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
    private bool _isInsideTreasureDungeon;

    // ============================================================
    // 历史记录 —— 预计算缓存（避免每帧 ToString + 字符串拼接）
    // ============================================================

    /// <summary>历史条目：在 Add 时一次性计算显示文本和颜色，Draw 时零分配</summary>
    private readonly struct HistoryEntry
    {
        public HistoryEntry() { }
        public string Value { get; init; } = "";
        public string DisplayText { get; init; } = "";
        public Vector4 Color { get; init; }
    }

    private readonly List<HistoryEntry> _results = new();

    /// <summary>非分割线条目计数，替代每 Add 时 LINQ Count</summary>
    private int _nonSeparatorCount;

    // ============================================================
    // 成就进度 —— 过滤结果缓存（避免每帧 ToList 分配）
    // ============================================================

    private bool _cachedTrackingEnabled;
    private readonly bool[] _cachedTracked = new bool[10];
    private List<AchievementProgressInfo> _cachedAchDisplay = new();
    private bool _achFilterValid;

    // ============================================================
    // 静态颜色常量（避免每帧 new Vector4）
    // ============================================================

    private static readonly Vector4 ColorWheelLow        = new(0.6f,  0.6f,  1.0f, 1);   // 蓝色 - 下级
    private static readonly Vector4 ColorWheelMedium     = new(0.4f,  1.0f,  0.6f, 1);   // 绿色 - 中级
    private static readonly Vector4 ColorWheelHigh       = new(1.0f,  0.3f,  0.3f, 1);   // 红色 - 上级
    private static readonly Vector4 ColorWheelShift      = new(1.0f,  0.8f,  0.2f, 1);   // 金色 - 变动
    private static readonly Vector4 ColorWheelSpecial    = new(0.75f, 0.75f, 0.8f, 1);   // 银色 - 特殊
    private static readonly Vector4 ColorWheelEnd        = new(0.8f,  0.4f,  1.0f, 1);   // 紫色 - 失败
    private static readonly Vector4 ColorGateOpen        = new(0.3f,  1.0f,  0.3f, 1);   // 亮绿 - 开门
    private static readonly Vector4 ColorGateFail        = new(1.0f,  0.3f,  0.3f, 1);   // 红色 - 开门失败
    private static readonly Vector4 ColorDungeonComplete = new(1.0f,  0.8f,  0.2f, 1);   // 金色 - 下底成功
    private static readonly Vector4 ColorDefault         = new(1,    1,    1,    1);
    private static readonly Vector4 ColorGray            = new(0.6f,  0.6f,  0.6f, 1);
    private static readonly Vector4 ColorTitleComplete   = new(0.2f,  1f,   0.2f, 1f);
    private static readonly Vector4 ColorTitleIncomplete = new(0.5f,  0.5f,  0.5f, 1f);

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

    // ============================================================
    // 历史记录管理
    // ============================================================

    /// <summary>从 DTO 构建预计算的 HistoryEntry（仅在 Add 时调用一次）</summary>
    private static HistoryEntry CreateEntry(TreasureResultDTO dto)
    {
        if (dto.Value == "separator")
            return new HistoryEntry { Value = "separator" };

        var text = dto.Value == "dungeon-complete"
            ? "❀❀下底成功❀❀"
            : ResultFormatter.GetTreasureResultText(dto.Value);
        var roundInfo = dto.Round > 0 ? $" (第{dto.Round}轮)" : "";
        var time = dto.Timestamp.ToString("HH:mm:ss");
        var sourcePrefix = string.IsNullOrEmpty(dto.Source) ? "" : $"[{dto.Source}] ";

        return new HistoryEntry
        {
            Value = dto.Value,
            DisplayText = $"[{time}] {sourcePrefix}{text}{roundInfo}",
            Color = GetHistoryColor(dto.Value)
        };
    }

    private static Vector4 GetHistoryColor(string value) => value switch
    {
        "wheel-low"         => ColorWheelLow,
        "wheel-medium"      => ColorWheelMedium,
        "wheel-high"        => ColorWheelHigh,
        "wheel-shift"       => ColorWheelShift,
        "wheel-special"     => ColorWheelSpecial,
        "wheel-end"         => ColorWheelEnd,
        "wheel-open"        => ColorGateOpen,
        "gate-open"         => ColorGateOpen,
        "gate-fail"         => ColorGateFail,
        "dungeon-complete"  => ColorDungeonComplete,
        _                   => ColorDefault
    };

    public void AddDutyCompleteSeparator()
    {
        _isInsideTreasureDungeon = false;
        _results.Add(CreateEntry(new TreasureResultDTO { Value = "dungeon-complete", Timestamp = DateTime.Now }));
        _results.Add(new HistoryEntry { Value = "separator" });
        _nonSeparatorCount++; // dungeon-complete 计入非分割线
    }

    public void AddResult(TreasureResultDTO dto)
    {
        // 检测新进挖宝图：首次收到非结束的转盘结果时插入分割线
        if (dto.Value.StartsWith("wheel-") && dto.Value != "wheel-end" && !_isInsideTreasureDungeon)
        {
            _isInsideTreasureDungeon = true;

            // 避免与 AddDutyCompleteSeparator 已插入的分割线重复
            if (!(_results.Count > 0 && _results[^1].Value == "separator"))
                _results.Add(new HistoryEntry { Value = "separator" });
        }

        _results.Add(CreateEntry(dto));
        _nonSeparatorCount++;

        // 检测挖宝结束
        if (dto.Value == "wheel-end")
        {
            _isInsideTreasureDungeon = false;
        }

        // 限制历史数量（分割线不计入限制），用计数器替代 LINQ Count
        var maxHistory = _plugin.Configuration.MaxHistoryCount;
        while (_nonSeparatorCount > maxHistory)
        {
            var idx = _results.FindIndex(r => r.Value != "separator");
            if (idx >= 0)
            {
                _results.RemoveAt(idx);
                _nonSeparatorCount--;
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
            _nonSeparatorCount = 0;
            _isInsideTreasureDungeon = false;
        }
        ImGuiHelpers.ScaledDummy(4);

        using var child = ImRaii.Child("##historyList", Vector2.Zero, true);
        if (!child.Success) return;

        // 倒序显示（最新的在上面）—— 使用预计算的 DisplayText 和 Color，零分配
        for (int i = _results.Count - 1; i >= 0; i--)
        {
            var entry = _results[i];

            // 分割线
            if (entry.Value == "separator")
            {
                ImGuiHelpers.ScaledDummy(2);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(2);
                continue;
            }

            ImGui.TextColored(entry.Color, entry.DisplayText);
        }
    }

    // ============================================================
    // 成就进度
    // ============================================================

    private void DrawAchievementProgress()
    {
        var achList = _plugin.Achievements;
        if (achList == null || achList.Count == 0)
        {
            ImGui.TextColored(ColorGray, "成就数据未就绪");
            return;
        }

        var cfg = _plugin.Configuration;

        // 检查过滤参数是否变化，仅在变化时重新计算（避免每帧 ToList 分配）
        if (!_achFilterValid || HasFilterChanged(cfg))
        {
            UpdateAchDisplayCache(cfg, achList);
        }

        if (_cachedAchDisplay.Count == 0)
        {
            ImGui.TextColored(ColorGray, "请在设置中选择要追踪的成就");
            return;
        }

        ImGui.BeginChild("##achievementList", Vector2.Zero, true);
        var barWidth = ImGui.GetContentRegionAvail().X;
        var first = true;

        foreach (var ach in _cachedAchDisplay)
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
                    var titleColor = ach.IsComplete ? ColorTitleComplete : ColorTitleIncomplete;
                    ImGui.TextColored(titleColor, ach.TitleName);
                }
            }
            else
            {
                ImGui.TextColored(ColorGray, "正在获取数据...");
            }

            ImGuiHelpers.ScaledDummy(4);
        }

        ImGui.EndChild();

        if (ImGui.Button("导出"))
        {
            _plugin.ExportAchievementProgress();
        }
    }

    /// <summary>检查成就过滤参数是否与缓存不一致</summary>
    private bool HasFilterChanged(Configuration cfg)
    {
        if (_cachedTrackingEnabled != cfg.EnableAchievementTracking) return true;
        if (!cfg.EnableAchievementTracking) return false;

        var tracked = cfg.TrackedAchievements;
        for (int i = 0; i < tracked.Length && i < _cachedTracked.Length; i++)
        {
            if (_cachedTracked[i] != tracked[i]) return true;
        }
        return false;
    }

    /// <summary>重新计算成就显示列表缓存</summary>
    private void UpdateAchDisplayCache(Configuration cfg, List<AchievementProgressInfo> achList)
    {
        _cachedTrackingEnabled = cfg.EnableAchievementTracking;
        var tracked = cfg.TrackedAchievements;
        for (int i = 0; i < tracked.Length && i < _cachedTracked.Length; i++)
            _cachedTracked[i] = tracked[i];

        if (cfg.EnableAchievementTracking)
        {
            _cachedAchDisplay = achList
                .Where((a, i) => i < tracked.Length && tracked[i])
                .ToList();
        }
        else
        {
            _cachedAchDisplay = achList.ToList();
        }
        _achFilterValid = true;
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
