using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;

namespace TreasureForecast.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration configuration;

    private static readonly Vector4 SectionColor = new(0, 1, 1, 1);

    public ConfigWindow(Plugin plugin) : base("挖宝预测 设置##TreasureForecastConfig")
    {
        Size = new Vector2(360, 420);
        SizeCondition = ImGuiCond.Always;

        this.plugin = plugin;
        this.configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var changed = false;

        ImGui.TextColored(SectionColor, "预测开关");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        changed |= DrawCheckbox("转盘结果预测 (G10/G12/G15)",
            () => configuration.EnableWheelPrediction, v => configuration.EnableWheelPrediction = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("开门/路结果预测",
            () => configuration.EnableGatePrediction, v => configuration.EnableGatePrediction = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("巡梦金库老虎机预测",
            () => configuration.EnableHypnoslot, v => configuration.EnableHypnoslot = v);

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextColored(SectionColor, "输出设置");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        changed |= DrawCheckbox("在聊天框显示结果",
            () => configuration.ShowInChat, v => configuration.ShowInChat = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("Toast2显示结果",
            () => configuration.ShowToastResult, v => configuration.ShowToastResult = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("副本完成时提示下底成功",
            () => configuration.ShowDungeonCompleteMessage, v => configuration.ShowDungeonCompleteMessage = v);

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextColored(SectionColor, "成就追踪");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        var tracking = configuration.EnableAchievementTracking;
        if (ImGui.Checkbox("启用自选成就进度追踪", ref tracking))
        {
            configuration.EnableAchievementTracking = tracking;
            changed = true;
        }

        if (tracking)
        {
            ImGuiHelpers.ScaledDummy(4);
            var achList = plugin.Achievements;
            var tracked = configuration.TrackedAchievements;
            for (int i = 0; i < achList.Count && i < tracked.Length; i++)
            {
                var t = tracked[i];
                if (ImGui.Checkbox(achList[i].AchievementName, ref t))
                {
                    tracked[i] = t;
                    changed = true;
                }
            }
        }

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextColored(new Vector4(1, 1, 0, 1), "调试");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        changed |= DrawCheckbox("Debug 日志输出（诊断网络数据包）",
            () => configuration.EnableDebugLog, v => configuration.EnableDebugLog = v);

        if (changed)
            configuration.Save();
    }

    private bool DrawCheckbox(string label, Func<bool> get, Action<bool> set)
    {
        var val = get();
        if (ImGui.Checkbox(label, ref val))
        {
            set(val);
            return true;
        }
        return false;
    }
}
