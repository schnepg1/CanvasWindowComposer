using System;

namespace CanvasDesktop;

/// <summary>
/// Immutable snapshot the <see cref="OverviewManager"/> pushes into the canvas dock on every refresh.
/// Lives outside the dock control so headless tests can build states without a WPF window.
/// </summary>
internal sealed record CanvasDockState(
    AnnotationMode Mode, string? Status = null, int SelectedCount = 0, CanvasAnnotation? Selected = null,
    string Color = "#FBBF24", double Stroke = 3, double FontSize = 18,
    AnnotationTextAlign TextAlign = AnnotationTextAlign.Left, AnnotationTextEmphasis Emphasis = AnnotationTextEmphasis.Normal,
    AnnotationCardFill CardFill = AnnotationCardFill.Solid, bool CanUndo = false, bool CanRedo = false,
    bool HasSelectedAnnotations = false, bool AnimateFeedback = true,
    bool CanCopy = false, bool CanCut = false, bool CanPaste = true);
