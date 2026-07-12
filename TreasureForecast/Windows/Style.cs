using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace TreasureForecast.Windows;

internal static class Style
{
    // 窗口样式常量
    internal static readonly Vector4 PixelWindowBg = new(0.06f, 0.06f, 0.08f, 0.96f);
    internal static readonly Vector4 PixelChildBg   = new(0.03f, 0.03f, 0.05f, 1f);
    internal static readonly Vector4 PixelBorder    = new(0.22f, 0.22f, 0.28f, 0.5f);
    internal static readonly Vector4 PixelDim       = new(0.35f, 0.35f, 0.4f, 1f);
    internal static readonly Vector4 PixelAccent    = new(0.4f, 0.85f, 1.0f, 1f);
    internal static readonly Vector4 PixelTabActive = new(0.12f, 0.12f, 0.16f, 1f);
    internal static readonly Vector4 PixelTabHover  = new(0.08f, 0.08f, 0.12f, 1f);
    internal static readonly Vector4 PixelButtonBg  = new(0.1f, 0.1f, 0.14f, 1f);

    // 历史结果颜色
    internal static readonly Vector4 ColorWheelLow        = new(0.5f,  0.6f,  1.0f, 1);
    internal static readonly Vector4 ColorWheelMedium     = new(0.3f,  0.9f,  0.5f, 1);
    internal static readonly Vector4 ColorRed             = new(1.0f,  0.4f,  0.4f, 1);
    internal static readonly Vector4 ColorGold            = new(1.0f,  0.78f, 0.25f, 1);
    internal static readonly Vector4 ColorWheelSpecial    = new(0.72f, 0.72f, 0.78f, 1);
    internal static readonly Vector4 ColorWheelEnd        = new(0.78f, 0.45f, 1.0f, 1);
    internal static readonly Vector4 ColorGateOpen        = new(0.3f,  0.9f,  0.35f, 1);
    internal static readonly Vector4 ColorDefault         = new(0.85f, 0.85f, 0.85f, 1);
    internal static readonly Vector4 ColorGray            = new(0.45f, 0.45f, 0.5f, 1);
    internal static readonly Vector4 ColorTitleComplete   = new(0.2f,  0.9f,  0.25f, 1);
    internal static readonly Vector4 ColorTitleIncomplete = new(0.45f, 0.45f, 0.5f, 1);

    // 进度条颜色
    internal static readonly uint ProgressColorComplete = ImGui.GetColorU32(new Vector4(0.2f, 0.9f, 0.25f, 1f));
    internal static readonly uint ProgressColorHigh     = ImGui.GetColorU32(new Vector4(0.3f, 0.7f, 1f,   1f));
    internal static readonly uint ProgressColorMid      = ImGui.GetColorU32(new Vector4(1f,  0.78f, 0.25f, 1f));
    internal static readonly uint ProgressColorLow      = ImGui.GetColorU32(new Vector4(1f,  0.35f, 0.35f, 1f));

    // Push/Pop 计数
    internal const int PushedColorCount = 10;
    internal const int PushedVarCount   = 8;
}
