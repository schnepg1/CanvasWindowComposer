using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private CanvasComposerWindow? _composerWindow;
    private ComposerPreviewLayer? _composerLayer;
    private readonly HashSet<Guid> _composerPreviewIds = [];
    internal CanvasComposerWindow? ComposerWindow => _composerWindow;

    private IReadOnlyList<CanvasAnnotation> CurrentComposerNotes() => _annotationDocument?.Items
        .Where(a => a.DesktopId == _annotationDesktop()).ToArray() ?? [];

    internal void OpenCanvasComposer(ICanvasComposerProvider? provider = null, IComposerCredentials? credentials = null, IChatGptAccounts? accounts = null)
    {
        if (_disposed || _annotationDocument == null || CurrentMode != OverviewMode.Zooming) return;
        if (_composerWindow != null) { _composerWindow.Activate(); return; }
        var selected=Selection.Ids;
        Guid? selectedId = selected.Count==0?_annotationTool?.SelectedId:null;
        SelectAnnotationMode(AnnotationMode.Navigate);
        var owner = _passes.FirstOrDefault(p => p.Visible && p.Screen.Primary) ?? _passes.FirstOrDefault(p => p.Visible);
        if (owner == null) return;
        var form = new CanvasComposerWindow(key => provider ?? new OpenAiCanvasComposerProvider(AiServices.Http, key), credentials ?? new ComposerCredentials(), accounts ?? AiServices.Accounts,
            BuildComposerContext, ApplyComposerPreview, CurrentComposerNotes(), selectedId, selected, CurrentComposerReferences());
        _composerWindow = form;
        form.PreviewChanged += preview =>
        {
            _composerPreviewIds.Clear();
            if (preview != null) foreach (var item in preview.Items) _composerPreviewIds.Add(item.Id);
            _composerLayer?.SetPreview(preview);
            if (preview != null) FrameComposerPreview(preview);
            RefreshToolPreview();
        };
        form.Dismissed += () => { CancelCanvasComposer(); RefreshToolPreview(); FocusOverview(); };
        var area = owner.Screen.WorkingArea;
        form.Size = new(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height - 80));
        form.Location = new(area.Right - form.Width - 12, area.Top + 24);
        form.Show(owner); form.Activate();
    }
    private IReadOnlyList<ComposerReference> CurrentComposerReferences() => SelectionItems().Where(i => i.Key.Kind!=CanvasItemKind.Annotation)
        .Select(i=>new ComposerReference(i.Key.Id,i.Key.Kind==CanvasItemKind.Window?"window":"project",i.Title,i.Bounds)).ToArray();

    private ComposerContext BuildComposerContext(IReadOnlyList<CanvasAnnotation> shared, IReadOnlyList<ComposerReference> references)
    {
        if (_annotationDocument == null || shared.Count+references.Count > CanvasComposer.MaximumContextItems
            || shared.Sum(a => a.Text.Length)+references.Sum(r=>r.Title.Length) > CanvasComposer.MaximumContextCharacters)
            throw new ArgumentException("Share up to 20 context items and 32,000 characters.");
        var desktop = _annotationDesktop();
        foreach (var item in shared)
            if (item.DesktopId != desktop || !_annotationDocument.Items.Contains(item))
                throw new InvalidOperationException("A shared note changed. Reopen the composer and try again.");
        var available=CurrentComposerReferences();
        if (references.Any(r=>!available.Contains(r))) throw new InvalidOperationException("A shared window or project changed. Reopen the composer.");
        var primary = _screens.PrimaryWorkingArea;
        var anchor = _camera.WorldFromVirtual(primary.X + 80, primary.Y + 100);
        var obstacles = _annotationDocument.Items.Where(a => a.DesktopId == desktop).Select(a => a.Bounds)
            .Concat(_windows.Windows.Where(w => !w.ScreenFixed).Select(w => new CanvasRect(w.World.X, w.World.Y, w.World.W, w.World.H))).ToArray();
        var selected=shared.Select(a=>a.Bounds).Concat(references.Select(r=>r.Bounds)).ToArray();
        var origin=selected.Length==0?new CanvasPoint(anchor.x,anchor.y):new CanvasPoint(selected.Min(r=>r.X),selected.Min(r=>r.Y));
        return new(desktop, _annotationDocument.Revision, origin, shared.ToArray(), obstacles) { SharedReferences=references.ToArray() };
    }
    private string ApplyComposerPreview(ComposerPreview preview)
    {
        if (_annotationDocument == null || _annotationDesktop() != preview.Desktop || CurrentMode != OverviewMode.Zooming)
            throw new InvalidOperationException("The desktop changed. Generate a fresh preview.");
        if (preview.References.Any(r=>!CurrentComposerReferences().Contains(r)))
            throw new InvalidOperationException("A shared window or project changed. Generate a fresh preview.");
        _annotationDocument.ApplyBatch(preview.Revision, preview.Desktop, preview.Items);
        return _annotationStore?.Status is {} warning ? "Applied in memory. " + warning : "Applied to canvas. Undo restores the entire previous state.";
    }
    private void FrameComposerPreview(ComposerPreview preview)
    {
        if (preview.Items.Count == 0) return;
        double left = preview.Items.Min(a => a.Bounds.X), top = preview.Items.Min(a => a.Bounds.Y);
        double width = Math.Max(100, preview.Items.Max(a => a.Bounds.X + a.Bounds.Width) - left);
        double height = Math.Max(100, preview.Items.Max(a => a.Bounds.Y + a.Bounds.Height) - top);
        var screen = _screens.PrimaryWorkingArea;
        double availableWidth = Math.Max(300, screen.Width - (_composerWindow?.Width ?? 0) - 60);
        double zoom = Math.Clamp(Math.Min(availableWidth / width, (screen.Height - 160) / height) * .85, OverviewCamera.ZoomMin, OverviewCamera.ZoomMax);
        _camera.SetTo(left + width / 2 - (screen.X + availableWidth / 2) / zoom,
            top + height / 2 - (screen.Y + screen.Height / 2) / zoom, zoom);
        _startupIntroLanded = false;
        ReconcileVisuals();
    }
    private bool CancelCanvasComposer()
    {
        if (_composerWindow is not {} form) return false;
        _composerWindow = null; form.Dispose(); _composerLayer?.SetPreview(null); _composerPreviewIds.Clear(); return true;
    }
}

internal sealed class ComposerPreviewLayer(Func<bool> visible) : IOverviewAnnotationLayer
{
    private CanvasAnnotationLayer? _layer;
    public void SetPreview(ComposerPreview? preview)
    {
        if (preview == null) { _layer = null; return; }
        var document = new AnnotationDocument(preview.Items);
        _layer = new(document, new AnnotationCanvasTool(document, () => preview.Desktop), () => preview.Desktop, visible);
    }
    public void Draw(System.Windows.Media.DrawingContext dc, OverviewAnnotationContext context)
    {
        if (_layer == null || !visible()) return;
        dc.PushOpacity(.6); _layer.Draw(dc, context); dc.Pop();
    }
}
