using System;
using System.Windows.Media;

namespace CanvasDesktop;

/// <summary>
/// Centralized Fluent dark palette and typography tokens for Mission Control.
/// </summary>
internal static class MissionControlTheme
{
    public static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public static readonly SolidColorBrush WindowBackground = Brush("#0F172A");
    public static readonly SolidColorBrush ForegroundBright = Brush("#F8FAFC");
    public static readonly SolidColorBrush CardBackground = Brush("#1E293B");
    public static readonly SolidColorBrush CardBorder = Brush("#334155");
    public static readonly SolidColorBrush CardRowBackground = Brush("#0F172A");
    public static readonly SolidColorBrush CardRowBorder = Brush("#334155");
    public static readonly SolidColorBrush SubtleBorder = Brush("#1E293B");
    public static readonly SolidColorBrush BannerBackground = Brush("#0B1120");
    public static readonly SolidColorBrush BannerBorder = Brush("#1E293B");
    public static readonly SolidColorBrush AvatarBackground = Brush("#1E3A8A");

    public static readonly SolidColorBrush TextPrimary = Brush("#F8FAFC");
    public static readonly SolidColorBrush TextSecondary = Brush("#E2E8F0");
    public static readonly SolidColorBrush TextMuted = Brush("#94A3B8");
    public static readonly SolidColorBrush TextSubtle = Brush("#64748B");
    public static readonly SolidColorBrush TextFeedback = Brush("#CBD5E1");

    public static readonly SolidColorBrush AccentBlue = Brush("#60A5FA");
    public static readonly SolidColorBrush AccentPurple = Brush("#A78BFA");

    public static SolidColorBrush AgentAccent(string hex) => Brush(hex);
}
