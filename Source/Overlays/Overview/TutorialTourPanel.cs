using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Screen-space practice prompts, also usable after the canvas closes. Buttons support Tab/Enter.</summary>
internal sealed class TutorialTourPanel : Form
{
    private readonly Label _progress=new() { AutoSize=false,Dock=DockStyle.Top,Height=24,ForeColor=CanvasDockTheme.Muted };
    private readonly Label _title=new() { AutoSize=false,Dock=DockStyle.Top,Height=36,ForeColor=CanvasDockTheme.Ink };
    private readonly Label _status=new() { AutoSize=false,Dock=DockStyle.Fill,ForeColor=CanvasDockTheme.Ink };
    private readonly FlowLayoutPanel _buttons=new() { Dock=DockStyle.Bottom,Height=46,WrapContents=true,Padding=new Padding(0,10,0,0) };
    private readonly Button _start,_explore,_back,_next,_skip,_recenter,_controls;
    internal event Action? StartRequested,ExploreRequested,BackRequested,NextRequested,SkipRequested,RecenterRequested,ControlsRequested;
    internal bool IsOffer { get; private set; }
    internal string StatusText => _status.Text;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x80;return p; } }
    internal TutorialTourPanel()
    {
        Text="Canvas tutorial";AccessibleName="Canvas guided tutorial";
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;KeyPreview=true;
        AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.Manual;DoubleBuffered=true;
        BackColor=CanvasDockTheme.Surface;ForeColor=CanvasDockTheme.Ink;Padding=new Padding(20);
        ClientSize=new Size(640,218);
        Font=new Font("Segoe UI",9.5f);
        _progress.Font=new Font("Segoe UI",9);
        _title.Font=new Font("Segoe UI",14,FontStyle.Bold);
        _status.Font=new Font("Segoe UI",10);
        Controls.Add(_status);Controls.Add(_title);Controls.Add(_progress);Controls.Add(_buttons);
        Button Add(string text,Action action,bool primary=false)
        {
            var b=new TourButton(primary) { Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
                Padding=new Padding(12,7,12,7),Margin=new Padding(0,0,6,4),AccessibleName=text.Replace("&","") };
            b.Click+=(_,_)=>action();_buttons.Controls.Add(b);return b;
        }
        _start=Add("&Start tour",()=>StartRequested?.Invoke(),true);
        _explore=Add("&Explore myself",()=>ExploreRequested?.Invoke());
        _back=Add("&Back",()=>BackRequested?.Invoke());
        _next=Add("&Next",()=>NextRequested?.Invoke(),true);
        _recenter=Add("Show &note",()=>RecenterRequested?.Invoke());
        _controls=Add("&Controls",()=>ControlsRequested?.Invoke());
        _skip=Add("S&kip tour",()=>SkipRequested?.Invoke());
    }
    internal void Offer(bool resume,string? error)
    {
        IsOffer=true;_progress.Text="CANVAS TOUR  ·  AT YOUR OWN PACE";
        _title.Text=resume?"Continue your canvas tour?":"Make room for your ideas";
        _status.Text=error??"Look around, move a window, and write a note. Then return to your desktop and learn how to open canvas again. You can leave at any time.";
        _start.Text=resume?"&Resume tour":"&Start tour";
        _start.Visible=_explore.Visible=true;
        _back.Visible=_next.Visible=_skip.Visible=_recenter.Visible=false;
        _controls.Visible=true;
        OrderButtons(_start,_explore,_controls);
        AcceptButton=_start;
    }
    internal void UpdateTour(TutorialTour tour,string? error,bool overviewHotkey=true,bool desktop=false)
    {
        IsOffer=false;
        string[] names=["Look around","Zoom in and out","Move a window","Write your first note","Return to your desktop","Your desktop is still yours","Open canvas again"];
        _progress.Text=$"CANVAS TOUR  ·  {(desktop?"DESKTOP":"CANVAS")}  ·  STEP {tour.Progress.Step+1} OF {TutorialTour.StepCount}";
        _title.Text=names[tour.Progress.Step];
        string reopen=overviewHotkey?"Press Alt+Q, or hold Alt and scroll out over an empty area of your desktop.":"Hold Alt and scroll out over an empty area of your desktop. The Alt+Q shortcut is turned off in Settings.";
        _status.Text=error??(tour.Travelling?"Moving to the next note. Any navigation input stops the movement."
            :tour.Practised && !(desktop && tour.Step==TutorialStep.ReopenCanvas)?tour.Step==TutorialStep.ReopenCanvas?"You're back in canvas. Your windows and notes are here, ready whenever you need more room. Choose Finish to explore.":"✓ You've got it. Choose Next when you're ready."
            :tour.Step switch
            {
                TutorialStep.Pan => "Hold Space and drag with the left mouse button to look around. On a touchpad, scroll with two fingers. You can also choose Next without practising.",
                TutorialStep.Zoom => "Hold Ctrl and scroll to make things bigger or smaller. You can also press + / −. Choose Next whenever you're ready.",
                TutorialStep.Windows => "Drag a window or app card to a new spot. Your canvas gives you room to arrange things. Next continues without moving anything.",
                TutorialStep.Notes => "Choose Text in the tool belt below, then click empty space and type. Press Ctrl+Enter to save. Next continues without making a note.",
                TutorialStep.Projects => "Your windows and notes share one canvas. Choose Return to desktop to close this view. The next two steps stay with you on the desktop.",
                TutorialStep.Desktop => "Canvas view is closed. Use your apps and desktop as usual; this guide stays here while you learn how to return. Choose Next for the shortcuts.",
                _ => reopen+" You can also choose Open canvas below."
            });
        _start.Visible=_explore.Visible=false;
        _back.Visible=_next.Visible=_skip.Visible=true;
        _recenter.Visible=_controls.Visible=!desktop;
        _back.Enabled=!tour.Travelling && tour.Progress.Step>0;
        _next.Enabled=!tour.Travelling;
        _next.Text=tour.Step==TutorialStep.Projects?"Return to &desktop":tour.Step==TutorialStep.ReopenCanvas?desktop?"&Open canvas":"&Finish":"&Next";
        _next.AccessibleName=_next.Text.Replace("&","");
        _recenter.Enabled=!tour.Travelling;
        _recenter.Visible=!desktop && tour.Step<TutorialStep.Desktop;
        OrderButtons(_back,_recenter,_controls,_skip,_next);
        AcceptButton=_next;
    }
    internal void FitToWidth(int width)
    {
        Width=Math.Max(1,width);
        int available=Math.Max(1,ClientSize.Width-Padding.Horizontal);
        int textHeight=TextRenderer.MeasureText(_status.Text,_status.Font,new Size(available,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;
        int titleHeight=TextRenderer.MeasureText(_title.Text,_title.Font,new Size(available,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl).Height;
        _title.Height=Math.Max((int)(36*DeviceDpi/96.0),titleHeight+6);
        _buttons.Height=_buttons.GetPreferredSize(new Size(available,0)).Height;
        ClientSize=new Size(ClientSize.Width,Padding.Vertical+_progress.Height+_title.Height+Math.Max((int)(66*DeviceDpi/96.0),textHeight+8)+_buttons.Height);
        PerformLayout();
    }
    private void OrderButtons(params Button[] buttons)
    {
        for(int i=0;i<buttons.Length;i++)_buttons.Controls.SetChildIndex(buttons[i],i);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using var outline=CanvasDockTheme.Outline(new(.5f,.5f,Width-1,Height-1),16*DeviceDpi/96f);
        using var pen=new Pen(CanvasDockTheme.Border);e.Graphics.DrawPath(pen,outline);
    }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if(Width<2 || Height<2)return;
        using var outline=CanvasDockTheme.Outline(new(0,0,Width,Height),16*DeviceDpi/96f);
        var previous=Region;Region=new Region(outline);previous?.Dispose();
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { _progress.Font.Dispose();_title.Font.Dispose();_status.Font.Dispose();Font.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed class TourButton(bool primary) : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using var outline=CanvasDockTheme.Outline(new(0,0,Width-1,Height-1),9*DeviceDpi/96f);
            bool highlighted=primary && Enabled;
            Color surface=highlighted?CanvasDockTheme.Active:CanvasDockTheme.Surface;
            using var brush=new SolidBrush(surface);e.Graphics.FillPath(brush,outline);
            using var pen=new Pen(Focused?CanvasDockTheme.Active:CanvasDockTheme.Border);e.Graphics.DrawPath(pen,outline);
            if(Focused)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),highlighted?CanvasDockTheme.ActiveInk:CanvasDockTheme.Ink,surface);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?primary?CanvasDockTheme.ActiveInk:CanvasDockTheme.Ink:CanvasDockTheme.Muted,
                TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
        }
    }
}
