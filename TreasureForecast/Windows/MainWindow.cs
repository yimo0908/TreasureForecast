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
        ImGui.PushStyleColor(ImGuiCol.WindowBg,       Style.PixelWindowBg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg,        Style.PixelChildBg);
        ImGui.PushStyleColor(ImGuiCol.Border,         Style.PixelBorder);
        ImGui.PushStyleColor(ImGuiCol.Separator,      Style.PixelDim);
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        Style.PixelChildBg);
        ImGui.PushStyleColor(ImGuiCol.Button,         Style.PixelButtonBg);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,  Style.PixelTabHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,   Style.PixelTabActive);
        ImGui.PushStyleColor(ImGuiCol.Text,           Style.ColorDefault);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram,  Style.PixelAccent);

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
        ImGui.PopStyleVar(Style.PushedVarCount);
        ImGui.PopStyleColor(Style.PushedColorCount);
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
        "wheel-low"         => Style.ColorWheelLow,
        "wheel-medium"      => Style.ColorWheelMedium,
        "wheel-high"        => Style.ColorRed,
        "wheel-shift"       => Style.ColorGold,
        "wheel-special"     => Style.ColorWheelSpecial,
        "wheel-end"         => Style.ColorWheelEnd,
        "wheel-open"        => Style.ColorGateOpen,
        "gate-open"         => Style.ColorGateOpen,
        "gate-fail"         => Style.ColorRed,
        "dungeon-complete"  => Style.ColorGold,
        "duty-wiped"        => Style.ColorRed,
        _                   => Style.ColorDefault
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
    /// 添加挖宝结果到历史记录。对开门/关门结果进行去重（对比当前会话最后一条记录）。
    /// 返回 false 表示因重复被跳过。
    /// </summary>
    public bool AddResult(TreasureResultDTO dto)
    {
        // 去重：仅对开门/关门结果检查当前会话最后一条记录是否重复
        // 最后一条是 separator（会话边界）时跳过，不跨会话比较
        if (dto.Value is "gate-open" or "gate-fail" && results.Count > 0)
        {
            var last = results[^1];
            if (last.Value != "separator" && last.Value == dto.Value && last.Round == dto.Round)
                return false;
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

            ImGui.PushStyleColor(ImGuiCol.Button,        active ? Style.PixelTabActive : new Vector4(0, 0, 0, 0));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? Style.PixelTabActive : Style.PixelTabHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  active ? Style.PixelTabActive : Style.PixelTabHover);
            ImGui.PushStyleColor(ImGuiCol.Text,          active ? Style.PixelAccent    : Style.PixelDim);

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
        ImGui.TextColored(Style.PixelDim, sb.ToString());
    }

    private void DrawPixelProgress(float ratio, uint current, uint max, string titleName, bool isComplete, uint titleId)
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
            ImGui.TextColored(isComplete ? Style.ColorTitleComplete : Style.ColorTitleIncomplete, titleName);

            if (isComplete && titleId != 0 && ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    plugin.SetTitle(titleId, titleName);
            }
        }
    }

    private void DrawSectionHeader(string title, Action? buttonAction = null)
    {
        ImGui.TextColored(Style.PixelAccent, title);
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

            ImGui.TextColored(Style.PixelDim, "»");
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
            ImGui.TextColored(Style.ColorGray, "成就数据未就绪");
            return;
        }

        var cfg = plugin.Configuration;

        if (!achFilterValid || HasFilterChanged(cfg))
            UpdateAchDisplayCache(cfg, achList);

        if (cachedAchDisplay.Count == 0)
        {
            ImGui.TextColored(Style.ColorGray, "请在设置中选择要追踪的成就");
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

            ImGui.TextColored(Style.ColorDefault, $"  {ach.AchievementName}");

            if (ach.Max > 0)
            {
                ImGuiHelpers.ScaledDummy(1);
                ImGui.Text("  ");
                ImGui.SameLine();
                DrawPixelProgress(ach.Ratio, ach.Current, ach.Max, ach.TitleName, ach.IsComplete, ach.TitleID);
            }
            else
            {
                ImGui.TextColored(Style.ColorGray, "  正在获取数据...");
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
        >= 1f    => Style.ProgressColorComplete,
        >= 0.5f  => Style.ProgressColorHigh,
        >= 0.25f => Style.ProgressColorMid,
        _        => Style.ProgressColorLow,
    };
}
