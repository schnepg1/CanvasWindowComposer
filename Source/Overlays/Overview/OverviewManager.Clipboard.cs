using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private int _annotationPasteOffsetCount;
    private bool _isPasting;

    public bool CopySelectedAnnotations()
    {
        if (_annotationDocument == null) return false;
        var selected = GetSelectedAnnotations();
        if (selected.Count == 0) return false;

        _annotationPasteOffsetCount = 0;
        try
        {
            string json = AnnotationClipboard.Serialize(selected);
            var data = new DataObject();
            data.SetData(AnnotationClipboard.DataFormat, json);

            var texts = selected
                .Where(a => a.Kind == AnnotationKind.Text && !string.IsNullOrWhiteSpace(a.Text))
                .Select(a => a.Text);
            string combinedText = string.Join(Environment.NewLine + Environment.NewLine, texts);
            if (!string.IsNullOrEmpty(combinedText))
            {
                data.SetData(DataFormats.UnicodeText, combinedText);
            }

            Clipboard.SetDataObject(data, copy: true, retryTimes: 3, retryDelay: 50);
            return true;
        }
        catch (ExternalException ex)
        {
            _annotationError = "Clipboard is busy: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _annotationError = "Failed to copy annotations: " + ex.Message;
            return false;
        }
    }

    public bool CutSelectedAnnotations()
    {
        if (!CopySelectedAnnotations()) return false;
        DeleteAnnotation();
        return true;
    }

    public bool PasteAnnotations(Point? screenPoint = null)
    {
        if (_annotationDocument == null) return false;
        var desktop = _annotationDesktop();

        IDataObject? data = null;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (ExternalException ex)
        {
            _annotationError = "Clipboard is busy: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _annotationError = "Failed to read clipboard: " + ex.Message;
            return false;
        }

        if (data == null) return false;

        // 1. Check for native Canvas annotations format
        try
        {
            if (data.GetDataPresent(AnnotationClipboard.DataFormat))
            {
                if (data.GetData(AnnotationClipboard.DataFormat) is string json &&
                    AnnotationClipboard.TryDeserialize(json, out var sourceItems) &&
                    sourceItems.Count > 0)
                {
                    int step = (_annotationPasteOffsetCount % 10) + 1;
                    _annotationPasteOffsetCount++;
                    double offset = step * AnnotationClipboard.DefaultPasteOffset;

                    var prepared = AnnotationClipboard.PrepareForPaste(sourceItems, desktop, offset, offset);
                    if (prepared.Count == 0) return false;

                    try
                    {
                        if (!_annotationDocument.Paste(desktop, prepared)) return false;
                    }
                    catch (InvalidOperationException ex)
                    {
                        _annotationError = ex.Message;
                        return false;
                    }

                    _isPasting = true;
                    try
                    {
                        Selection.Replace(prepared.Select(p => new CanvasItemId(CanvasItemKind.Annotation, p.Id)));
                        if (prepared.Count == 1) _annotationTool?.Select(prepared[0].Id);
                        else _annotationTool?.ClearSelection();
                    }
                    finally
                    {
                        _isPasting = false;
                    }

                    _annotationStore?.Save();
                    ReconcileConnectedArrows();
                    RefreshToolPreview();
                    FocusOverview();
                    return true;
                }
            }
        }
        catch (ExternalException ex)
        {
            _annotationError = "Clipboard is busy: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _annotationError = "Failed to paste annotations: " + ex.Message;
            return false;
        }

        // 2. Fallback to plain text on clipboard
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                string? text = data.GetData(DataFormats.UnicodeText) as string
                    ?? data.GetData(DataFormats.Text) as string;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    CanvasPoint targetPoint;
                    if (screenPoint is { } sp)
                    {
                        var (wx, wy) = _camera.WorldFromVirtual(sp.X, sp.Y);
                        targetPoint = new CanvasPoint(wx, wy);
                    }
                    else
                    {
                        var (cx, cy) = _camera.WorldFromVirtual(
                            _screens.PrimaryWorkingArea.X + _screens.PrimaryWorkingArea.Width / 2,
                            _screens.PrimaryWorkingArea.Y + _screens.PrimaryWorkingArea.Height / 2);
                        int step = (_annotationPasteOffsetCount % 10);
                        _annotationPasteOffsetCount++;
                        double offset = step * AnnotationClipboard.DefaultPasteOffset;
                        targetPoint = new CanvasPoint(cx + offset, cy + offset);
                    }

                    var tool = _annotationTool;
                    var item = AnnotationClipboard.CreateFromPlainText(
                        text,
                        desktop,
                        targetPoint,
                        color: tool?.Color ?? "#FBBF24",
                        stroke: tool?.Stroke ?? 3,
                        fontSize: tool?.FontSize ?? AnnotationDocument.DefaultFontSize,
                        textAlign: tool?.TextAlign ?? AnnotationTextAlign.Left,
                        emphasis: tool?.Emphasis ?? AnnotationTextEmphasis.Normal,
                        cardFill: tool?.CardFill ?? AnnotationCardFill.Solid);

                    item = AnnotationVisual.FitText(item);

                    try
                    {
                        if (!_annotationDocument.Paste(desktop, [item])) return false;
                    }
                    catch (InvalidOperationException ex)
                    {
                        _annotationError = ex.Message;
                        return false;
                    }

                    _isPasting = true;
                    try
                    {
                        Selection.Replace([new CanvasItemId(CanvasItemKind.Annotation, item.Id)]);
                        _annotationTool?.Select(item.Id);
                    }
                    finally
                    {
                        _isPasting = false;
                    }

                    _annotationStore?.Save();
                    RefreshToolPreview();
                    FocusOverview();
                    return true;
                }
            }
        }
        catch (ExternalException ex)
        {
            _annotationError = "Clipboard is busy: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _annotationError = "Failed to paste text: " + ex.Message;
            return false;
        }

        return false;
    }

    private List<CanvasAnnotation> GetSelectedAnnotations()
    {
        if (_annotationDocument == null) return [];
        var currentDesktop = _annotationDesktop();
        var selectedIds = Selection.Ids
            .Where(id => id.Kind == CanvasItemKind.Annotation)
            .Select(id => id.Id)
            .ToHashSet();

        if (_annotationTool?.SelectedId is { } singleId)
        {
            selectedIds.Add(singleId);
        }

        return _annotationDocument.Items
            .Where(a => a.DesktopId == currentDesktop && selectedIds.Contains(a.Id) && string.IsNullOrEmpty(a.AgentTaskId))
            .ToList();
    }
}
