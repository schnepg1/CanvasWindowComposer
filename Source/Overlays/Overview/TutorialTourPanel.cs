using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Screen-space practice prompts and access to the full controls reference. Buttons support Tab/Enter.</summary>
internal sealed class TutorialTourPanel : Form
{
    private readonly Label _title=new() { AutoSize=false,Dock=DockStyle.Top,Height=25,ForeColor=Color.White };
    private readonly Label _status=new() { AutoSize=false,Dock=DockStyle.Fill,ForeColor=Color.Gainsboro };
    private readonly FlowLayoutPanel _buttons=new() { Dock=DockStyle.Bottom,Height=34,WrapContents=false };
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
        AutoScaleMode=AutoScaleMode.Dpi;StartPosition=FormStartPosition.Manual;
        BackColor=Color.FromArgb(28,34,46);ForeColor=Color.White;Padding=new Padding(10);
        ClientSize=new Size(560,138);
        _title.Font=new Font("Segoe UI",10,FontStyle.Bold);
        _status.Font=new Font("Segoe UI",9);
        Controls.Add(_status);Controls.Add(_title);Controls.Add(_buttons);
        Button Add(string text,Action action)
        {
            var b=new Button { Text=text,AutoSize=true,Height=28,FlatStyle=FlatStyle.Flat,AccessibleName=text.Replace("&","") };
            b.Click+=(_,_)=>action();_buttons.Controls.Add(b);return b;
        }
        _start=Add("&Start tour",()=>StartRequested?.Invoke());
        _explore=Add("&Explore myself",()=>ExploreRequested?.Invoke());
        _back=Add("&Back",()=>BackRequested?.Invoke());
        _next=Add("&Next",()=>NextRequested?.Invoke());
        _recenter=Add("Show &note",()=>RecenterRequested?.Invoke());
        _controls=Add("&Controls",()=>ControlsRequested?.Invoke());
        _skip=Add("S&kip tour",()=>SkipRequested?.Invoke());
        _start.BackColor=_next.BackColor=Color.FromArgb(28,78,160);
    }
    internal void Offer(bool resume,string? error)
    {
        IsOffer=true;_title.Text=resume?"Continue your canvas tour?":"Explore your canvas with a short tour";
        _status.Text=error??"One small step at a time. Try each action, or just choose Next.\nYou can leave the tour whenever you like.";
        _start.Text=resume?"&Resume tour":"&Start tour";
        _start.Visible=_explore.Visible=true;
        _back.Visible=_next.Visible=_skip.Visible=_recenter.Visible=false;
        _controls.Visible=true;
        OrderButtons(_start,_explore,_controls);
        AcceptButton=_start;
    }
    internal void UpdateTour(TutorialTour tour,string? error,bool overviewHotkey=true)
    {
        IsOffer=false;
        string[] names=["Look around","Zoom in and out","Move a window","Write your first note","Ready to explore"];
        _title.Text=$"{tour.Progress.Step+1} / {TutorialTour.StepCount}  ·  {names[tour.Progress.Step]}";
        _status.Text=error??(tour.Travelling?"Moving to the next note. Any navigation input stops the movement."
            :tour.Practised?"✓ You've got it. Choose Next when you're ready."
            :tour.Step switch
            {
                TutorialStep.Pan => "Hold Space and drag with the left mouse button to look around.\nOn a touchpad, scroll with two fingers. Next continues without practising.",
                TutorialStep.Zoom => "Hold Ctrl and scroll to make things bigger or smaller.\nYou can also press + / −. Next continues without practising.",
                TutorialStep.Windows => "Drag a window or app card to a new spot.\nNext continues without moving anything.",
                TutorialStep.Notes => "Choose Text in the bottom toolbar, then click empty space and type.\nPress Ctrl+Enter to save. Next continues without making a note.",
                _ => "Finish to explore. Press Esc until your desktop returns.\n"+
                    (overviewHotkey?"Alt+Q reopens the canvas.":"Alt+scroll on the desktop reopens the canvas.")+" More help: Controls (top right)."
            });
        _start.Visible=_explore.Visible=false;
        _back.Visible=_next.Visible=_skip.Visible=_recenter.Visible=_controls.Visible=true;
        _back.Enabled=!tour.Travelling && tour.Progress.Step>0;
        _next.Enabled=!tour.Travelling;
        _next.Text=tour.Step==TutorialStep.Projects?"&Finish":"&Next";
        _recenter.Enabled=!tour.Travelling;
        OrderButtons(_back,_recenter,_controls,_skip,_next);
        AcceptButton=_next;
    }
    private void OrderButtons(params Button[] buttons)
    {
        // Showing previously hidden native controls can change their z-order.
        // Keep the primary action in a predictable place as the offer becomes a tour.
        for(int i=0;i<buttons.Length;i++)_buttons.Controls.SetChildIndex(buttons[i],i);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { _title.Font.Dispose();_status.Font.Dispose(); }
        base.Dispose(disposing);
    }
}
