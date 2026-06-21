using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;

namespace TreasureForecast.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly Configuration _configuration;

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

        // ---- 预测开关 ----
        ImGui.TextColored(new Vector4(0, 1, 1, 1), "预测开关");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        var wheel = _configuration.EnableWheelPrediction;
        if (ImGui.Checkbox("转盘结果预测 (G10/G12/G15)", ref wheel))
        {
            _configuration.EnableWheelPrediction = wheel;
            changed = true;
        }
        ImGuiHelpers.ScaledDummy(2);

        var gate = _configuration.EnableGatePrediction;
        if (ImGui.Checkbox("开门/路结果预测", ref gate))
        {
            _configuration.EnableGatePrediction = gate;
            changed = true;
        }
        ImGuiHelpers.ScaledDummy(2);

        var hypno = _configuration.EnableHypnoslot;
        if (ImGui.Checkbox("巡梦金库老虎机预测", ref hypno))
        {
            _configuration.EnableHypnoslot = hypno;
            changed = true;
        }

        ImGuiHelpers.ScaledDummy(8);

        // ---- 显示设置 ----
        ImGui.TextColored(new Vector4(0, 1, 1, 1), "输出设置");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        var chat = _configuration.ShowInChat;
        if (ImGui.Checkbox("在聊天框显示结果", ref chat))
        {
            _configuration.ShowInChat = chat;
            changed = true;
        }
        ImGuiHelpers.ScaledDummy(2);

        var toast = _configuration.ShowToastResult;
        if (ImGui.Checkbox("Toast2显示结果", ref toast))
        {
            _configuration.ShowToastResult = toast;
            changed = true;
        }
        ImGuiHelpers.ScaledDummy(2);

        var dungeonComplete = _configuration.ShowDungeonCompleteMessage;
        if (ImGui.Checkbox("副本完成时提示下底成功", ref dungeonComplete))
        {
            _configuration.ShowDungeonCompleteMessage = dungeonComplete;
            changed = true;
        }

        ImGuiHelpers.ScaledDummy(8);

        // ---- 成就追踪 ----
        ImGui.TextColored(new Vector4(0, 1, 1, 1), "成就追踪");
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

        // ---- 调试 ----
        ImGui.TextColored(new Vector4(1, 1, 0, 1), "调试");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        var debugLog = _configuration.EnableDebugLog;
        if (ImGui.Checkbox("Debug 日志输出（诊断网络数据包）", ref debugLog))
        {
            _configuration.EnableDebugLog = debugLog;
            changed = true;
        }

        if (changed)
        {
            _configuration.Save();
        }
    }
}
