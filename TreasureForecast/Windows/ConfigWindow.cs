using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility;

namespace TreasureForecast.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration _configuration;

    public ConfigWindow(Plugin plugin) : base("挖宝预测 设置##TreasureForecastConfig")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(360, 360);
        SizeCondition = ImGuiCond.Always;

        _configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        if (_configuration.IsConfigWindowMovable)
            Flags &= ~ImGuiWindowFlags.NoMove;
        else
            Flags |= ImGuiWindowFlags.NoMove;
    }

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
        ImGui.TextColored(new Vector4(0, 1, 1, 1), "显示设置");
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4);

        var chat = _configuration.ShowInChat;
        if (ImGui.Checkbox("在聊天框显示结果", ref chat))
        {
            _configuration.ShowInChat = chat;
            changed = true;
        }
        ImGuiHelpers.ScaledDummy(2);

        var history = _configuration.ShowHistory;
        if (ImGui.Checkbox("显示历史记录", ref history))
        {
            _configuration.ShowHistory = history;
            changed = true;
        }
        ImGuiHelpers.ScaledDummy(2);

        var movable = _configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("可移动设置窗口", ref movable))
        {
            _configuration.IsConfigWindowMovable = movable;
            changed = true;
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
