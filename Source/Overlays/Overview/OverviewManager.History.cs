using System;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed partial class OverviewManager
{
    private void OnZoneLayoutRecorded(Guid desktop) => _annotationDocument?.ClearRedo(desktop);

    private bool ReplayCanvasHistory(bool redo)
    {
        long annotations=_annotationDocument?.HistoryOrder(_annotationDesktop(),redo)??0;
        long projects=_placeholderWorkspace?.ZoneHistoryOrder(redo)??0;
        if(CanvasHistoryOrder.PreferAnnotations(annotations,projects,redo)) return _annotationDocument!.Undo(_annotationDesktop(),redo);
        return projects>0 && (redo?_placeholderWorkspace!.RedoZoneLayout():_placeholderWorkspace!.UndoZoneLayout());
    }

    private bool HandleCanvasHistoryKey(KeyEventArgs e)
    {
        if(_annotationEditor!=null || _composerWindow!=null || !e.Control || e.Alt || e.KeyCode is not (Keys.Z or Keys.Y)) return false;
        bool redo=e.KeyCode==Keys.Y || e.Shift;
        if((_annotationDocument?.HistoryOrder(_annotationDesktop(),redo)??0)==0 && (_placeholderWorkspace?.ZoneHistoryOrder(redo)??0)==0) return false;
        AnnotationHistory(redo);e.Handled=true;e.SuppressKeyPress=true;return true;
    }
}
