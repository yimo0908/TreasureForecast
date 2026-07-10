using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
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
    private readonly Plugin plugin;

    // 样式常量
    private static readonly Vector4 PixelWindowBg = new(0.06f, 0.06f, 0.08f, 0.96f);
    private static readonly Vector4 PixelChildBg   = new(0.03f, 0.03f, 0.05f, 1f);
    private static readonly Vector4 PixelBorder    = new(0.22f, 0.22f, 0.28f, 0.5f);
    private static readonly Vector4 PixelDim       = new(0.35f, 0.35f, 0.4f, 1f);
    private static readonly Vector4 PixelAccent    = new(0.4f, 0.85f, 1.0f, 1f);
    private static readonly Vector4 PixelTabActive = new(0.12f, 0.12f, 0.16f, 1f);
    private static readonly Vector4 PixelTabHover  = new(0.08f, 0.08f, 0.12f, 1f);
    private static readonly Vector4 PixelButtonBg  = new(0.1f, 0.1f, 0.14f, 1f);

    // 历史结果颜色
    private static readonly Vector4 ColorWheelLow     = new(0.5f,  0.6f,  1.0f, 1);
    private static readonly Vector4 ColorWheelMedium  = new(0.3f,  0.9f,  0.5f, 1);
    private static readonly Vector4 ColorRed          = new(1.0f,  0.4f,  0.4f, 1);
    private static readonly Vector4 ColorGold         = new(1.0f,  0.78f, 0.25f, 1);
    private static readonly Vector4 ColorWheelSpecial = new(0.72f, 0.72f, 0.78f, 1);
    private static readonly Vector4 ColorWheelEnd     = new(0.78f, 0.45f, 1.0f, 1);
    private static readonly Vector4 ColorGateOpen     = new(0.3f,  0.9f,  0.35f, 1);
    private static readonly Vector4 ColorDefault      = new(0.85f, 0.85f, 0.85f, 1);
    private static readonly Vector4 ColorGray         = new(0.45f, 0.45f, 0.5f, 1);
    private static readonly Vector4 ColorTitleComplete   = new(0.2f,  0.9f,  0.25f, 1);
    private static readonly Vector4 ColorTitleIncomplete = new(0.45f, 0.45f, 0.5f, 1);

    private const int PushedColorCount = 10;
    private const int PushedVarCount = 8;

    private readonly struct HistoryEntry
    {
        public HistoryEntry() { }
        public string Value { get; init; } = "";
        public string DisplayText { get; init; } = "";
        public Vector4 Color { get; init; }
        public int Round { get; init; }
    }

    private readonly List<HistoryEntry> results = new();
    private int nonSeparatorCount;

    // 成就进度过滤缓存
    private bool cachedTrackingEnabled;
    private readonly bool[] cachedTracked = new bool[10];
    private List<AchievementProgressInfo> cachedAchDisplay = new();
    private bool achFilterValid;

    private int currentTab;
    private readonly StringBuilder sb = new();

    private static readonly string[] TabLabels = { "历史", "成就" };

    private static readonly uint ProgressColorComplete = ImGui.GetColorU32(new Vector4(0.2f,  0.9f,  0.25f, 1f));
    private static readonly uint ProgressColorHigh     = ImGui.GetColorU32(new Vector4(0.3f,  0.7f,  1f,   1f));
    private static readonly uint ProgressColorMid      = ImGui.GetColorU32(new Vector4(1f,   0.78f, 0.25f, 1f));
    private static readonly uint ProgressColorLow      = ImGui.GetColorU32(new Vector4(1f,   0.35f, 0.35f, 1f));

    public MainWindow(Plugin plugin)
        : base("挖宝预测##TreasureForecastMain", ImGuiWindowFlags.NoScrollbar)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;

        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new(1),
            Click = _ => plugin.ToggleConfigUi()
        });
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg,       PixelWindowBg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,        PixelChildBg);
        ImGui.PushStyleColor(ImGuiCol.Border,         PixelBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator,      PixelDim);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        PixelChildBg);
        ImGui.PushStyleColor(ImGuiCol.Button,         PixelButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,  PixelTabHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,   PixelTabActive);
        ImGui.PushStyleColor(ImGuiCol.Text,           ColorDefault);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram,  PixelAccent);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,    0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,      0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding,       0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize,   1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize,  1f);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(PushedVarCount);
        ImGui.PopStyleColor(PushedColorCount);
    }

    private static HistoryEntry CreateEntry(TreasureResultDTO dto)
    {
        var text = ResultFormatter.GetTreasureResultText(dto.Value);
        var roundInfo = dto.Round > 0 ? $" (第{dto.Round}轮)" : "";
        var time = dto.Timestamp.ToString("HH:mm:ss");
        var sourcePrefix = string.IsNullOrEmpty(dto.Source) ? "" : $"[{dto.Source}] ";

        return new HistoryEntry
        {
            Value = dto.Value,
            DisplayText = $"[{time}] {sourcePrefix}{text}{roundInfo}",
            Color = GetHistoryColor(dto.Value),
            Round = dto.Round
        };
    }

    private static Vector4 GetHistoryColor(string value) => value switch
    {
        "wheel-low"         => ColorWheelLow,
        "wheel-medium"      => ColorWheelMedium,
        "wheel-high"        => ColorRed,
        "wheel-shift"       => ColorGold,
        "wheel-special"     => ColorWheelSpecial,
        "wheel-end"         => ColorWheelEnd,
        "wheel-open"        => ColorGateOpen,
        "gate-open"         => ColorGateOpen,
        "gate-fail"         => ColorRed,
        "dungeon-complete"  => ColorGold,
        "duty-wiped"        => ColorRed,
        _                   => ColorDefault
    };

    /// <summary>
    /// 添加历史分割线。仅在已有非分割线条目且最后一条不是分割线时添加。
    /// </summary>
    public void AddSeparator()
    {
        if (nonSeparatorCount == 0) return;
        if (results.Count > 0 && results[^1].Value == "separator") return;
        results.Add(new HistoryEntry { Value = "separator" });
    }

    /// <summary>
    /// 添加副本事件条目（下底成功/团灭）到历史记录。
    /// </summary>
    public void AddDutyEventEntry(string value)
    {
        results.Add(CreateEntry(new TreasureResultDTO { Value = value, Timestamp = DateTime.Now }));
        nonSeparatorCount++;
    }

    /// <summary>
    /// 添加挖宝结果到历史记录。对开门/关门结果进行去重（对比上一条非分割线记录）。
    /// 返回 false 表示因重复被跳过。
    /// </summary>
    public bool AddResult(TreasureResultDTO dto)
    {
        // 去重：仅对开门/关门结果检查上一条是否重复
        if (dto.Value is "gate-open" or "gate-fail")
        {
            for (int i = results.Count - 1; i >= 0; i--)
            {
                var entry = results[i];
                if (entry.Value == "separator") continue;
                if (entry.Value == dto.Value && entry.Round == dto.Round)
                    return false;
                break;
            }
        }

        results.Add(CreateEntry(dto));
        nonSeparatorCount++;

        var maxHistory = plugin.Configuration.MaxHistoryCount;
        while (nonSeparatorCount > maxHistory)
        {
            var idx = results.FindIndex(r => r.Value != "separator");
            if (idx >= 0)
            {
                results.RemoveAt(idx);
                nonSeparatorCount--;
            }
            else break;
        }

        return true;
    }

    /// <summary>
    /// 获取最后一条非分割线记录的轮数。无记录时返回 0。
    /// </summary>
    public int GetLastNonSeparatorRound()
    {
        for (int i = results.Count - 1; i >= 0; i--)
        {
            if (results[i].Value != "separator")
                return results[i].Round;
        }
        return 0;
    }

    public override void Draw()
    {
        using var fontScope = Plugin.PluginInterface.UiBuilder.MonoFontHandle.Push();

        DrawPixelTabs();
        ImGuiHelpers.ScaledDummy(2);

        switch (currentTab)
        {
            case 0: DrawHistory(); break;
            case 1: DrawAchievementProgress(); break;
        }
    }

    private void DrawPixelTabs()
    {
        for (int i = 0; i < TabLabels.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var active = i == currentTab;

            ImGui.PushStyleColor(ImGuiCol.Button,        active ? PixelTabActive : new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? PixelTabActive : PixelTabHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  active ? PixelTabActive : PixelTabHover);
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? PixelAccent    : PixelDim);

            if (ImGui.SmallButton($" {TabLabels[i]} ##pixelTab{i}"))
                currentTab = i;

            ImGui.PopStyleColor(4);
        }
    }

    private void DrawPixelSeparator()
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var charWidth = ImGui.CalcTextSize("─").X;
        if (charWidth <= 0) return;
        var count = Math.Max(1, (int)(avail / charWidth));

        sb.Clear();
        sb.Append('─', count);
        ImGui.TextColored(PixelDim, sb.ToString());
    }

    private void DrawPixelProgress(float ratio, uint current, uint max, string titleName, bool isComplete)
    {
        const int barWidth = 16;
        var filled = Math.Clamp((int)Math.Round(ratio * barWidth), 0, barWidth);

        var barColor = ImGui.ColorConvertU32ToFloat4(GetProgressColor(ratio));

        sb.Clear();
        sb.Append('[');
        sb.Append('█', filled);
        sb.Append('░', barWidth - filled);
        sb.Append("] ");
        sb.Append(current);
        sb.Append(" / ");
        sb.Append(max);
        sb.Append(" (");
        sb.Append((int)(ratio * 100));
        sb.Append("%)");

        ImGui.TextColored(barColor, sb.ToString());

        if (titleName.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(isComplete ? ColorTitleComplete : ColorTitleIncomplete, titleName);
        }
    }

    private void DrawSectionHeader(string title, Action? buttonAction = null)
    {
        ImGui.TextColored(PixelAccent, title);
        ImGui.SameLine();
        buttonAction?.Invoke();
        ImGuiHelpers.ScaledDummy(2);
        DrawPixelSeparator();
        ImGuiHelpers.ScaledDummy(2);
    }

    private void DrawHistory()
    {
        DrawSectionHeader("▌ 历史记录", () =>
        {
            if (ImGui.SmallButton("导出"))
                ExportHistory();
            ImGui.SameLine();
            if (ImGui.SmallButton("清空"))
            {
                results.Clear();
                nonSeparatorCount = 0;
            }
        });

        using var child = ImRaii.Child("##historyList", Vector2.Zero, true);
        if (!child.Success) return;

        for (int i = results.Count - 1; i >= 0; i--)
        {
            var entry = results[i];

            if (entry.Value == "separator")
            {
                ImGuiHelpers.ScaledDummy(1);
                DrawPixelSeparator();
                ImGuiHelpers.ScaledDummy(1);
                continue;
            }

            if (entry.Value is "dungeon-complete" or "duty-wiped")
            {
                ImGui.TextColored(entry.Color, $"  {entry.DisplayText}");
                continue;
            }

            ImGui.TextColored(PixelDim, "»");
            ImGui.SameLine();
            ImGui.TextColored(entry.Color, entry.DisplayText);
        }
    }

    private void ExportHistory()
    {
        if (results.Count == 0)
        {
            Plugin.Chat.PrintError("没有可导出的历史记录");
            return;
        }

        var sb = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
        {
            var entry = results[i];
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(entry.Value == "separator" ? "====================" : entry.DisplayText);
        }

        ImGui.SetClipboardText(sb.ToString());
        Plugin.Chat.Print("历史记录已导出到剪贴板");
    }

    private void DrawAchievementProgress()
    {
        var achList = plugin.Achievements;
        if (achList == null || achList.Count == 0)
        {
            ImGui.TextColored(ColorGray, "成就数据未就绪");
            return;
        }

        var cfg = plugin.Configuration;

        if (!achFilterValid || HasFilterChanged(cfg))
            UpdateAchDisplayCache(cfg, achList);

        if (cachedAchDisplay.Count == 0)
        {
            ImGui.TextColored(ColorGray, "请在设置中选择要追踪的成就");
            return;
        }

        DrawSectionHeader("▌ 成就进度", () =>
        {
            if (ImGui.SmallButton("导出"))
                plugin.ExportAchievementProgress();
        });

        using var child = ImRaii.Child("##achievementList", Vector2.Zero, true);
        if (!child.Success) return;

        var first = true;

        foreach (var ach in cachedAchDisplay)
        {
            if (!first)
            {
                ImGuiHelpers.ScaledDummy(2);
                DrawPixelSeparator();
                ImGuiHelpers.ScaledDummy(2);
            }
            first = false;

            ImGui.TextColored(ColorDefault, $"  {ach.AchievementName}");

            if (ach.Max > 0)
            {
                ImGuiHelpers.ScaledDummy(1);
                ImGui.Text("  ");
                ImGui.SameLine();
                DrawPixelProgress(ach.Ratio, ach.Current, ach.Max, ach.TitleName, ach.IsComplete);
            }
            else
            {
                ImGui.TextColored(ColorGray, "  正在获取数据...");
            }
        }
    }

    private bool HasFilterChanged(Configuration cfg)
    {
        if (cachedTrackingEnabled != cfg.EnableAchievementTracking) return true;
        if (!cfg.EnableAchievementTracking) return false;

        var tracked = cfg.TrackedAchievements;
        for (int i = 0; i < tracked.Length && i < cachedTracked.Length; i++)
        {
            if (cachedTracked[i] != tracked[i]) return true;
        }
        return false;
    }

    private void UpdateAchDisplayCache(Configuration cfg, List<AchievementProgressInfo> achList)
    {
        cachedTrackingEnabled = cfg.EnableAchievementTracking;
        var tracked = cfg.TrackedAchievements;
        for (int i = 0; i < tracked.Length && i < cachedTracked.Length; i++)
            cachedTracked[i] = tracked[i];

        cachedAchDisplay = cfg.EnableAchievementTracking
            ? achList.Where((a, i) => i < tracked.Length && tracked[i]).ToList()
            : achList.ToList();
        achFilterValid = true;
    }

    private static uint GetProgressColor(float progress) => progress switch
    {
        >= 1f    => ProgressColorComplete,
        >= 0.5f  => ProgressColorHigh,
        >= 0.25f => ProgressColorMid,
        _        => ProgressColorLow,
    };
}
