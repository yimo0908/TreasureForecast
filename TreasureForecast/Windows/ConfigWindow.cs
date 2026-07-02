using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;

namespace TreasureForecast.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;

    private static readonly Vector4 SectionColor = new(0, 1, 1, 1);

    public ConfigWindow(Plugin plugin) : base("挖宝预测 设置##TreasureForecastConfig")
    {
        Size = new Vector2(360, 420);
        SizeCondition = ImGuiCond.Always;

        _plugin = plugin;
        _configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var changed = false;

        ImGui.TextColored(SectionColor, "预测开关");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        changed |= DrawCheckbox("转盘结果预测 (G10/G12/G15)",
            () => _configuration.EnableWheelPrediction, v => _configuration.EnableWheelPrediction = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("开门/路结果预测",
            () => _configuration.EnableGatePrediction, v => _configuration.EnableGatePrediction = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("巡梦金库老虎机预测",
            () => _configuration.EnableHypnoslot, v => _configuration.EnableHypnoslot = v);

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextColored(SectionColor, "输出设置");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        changed |= DrawCheckbox("在聊天框显示结果",
            () => _configuration.ShowInChat, v => _configuration.ShowInChat = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("Toast2显示结果",
            () => _configuration.ShowToastResult, v => _configuration.ShowToastResult = v);
        ImGuiHelpers.ScaledDummy(2);

        changed |= DrawCheckbox("副本完成时提示下底成功",
            () => _configuration.ShowDungeonCompleteMessage, v => _configuration.ShowDungeonCompleteMessage = v);

        ImGuiHelpers.ScaledDummy(8);

        ImGui.TextColored(SectionColor, "成就追踪");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        var tracking = _configuration.EnableAchievementTracking;
        if (ImGui.Checkbox("启用自选成就进度追踪", ref tracking))
        {
            _configuration.EnableAchievementTracking = tracking;
            changed = true;
        }

        if (tracking)
        {
            ImGuiHelpers.ScaledDummy(4);
            var achList = _plugin.Achievements;
            var tracked = _configuration.TrackedAchievements;
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
            () => _configuration.EnableDebugLog, v => _configuration.EnableDebugLog = v);

        if (changed)
            _configuration.Save();
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
