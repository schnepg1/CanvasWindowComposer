using System;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>Screen-space controls; explanations remain on the canvas. Standard buttons support Tab/Enter.</summary>
internal sealed class TutorialTourPanel : Form
{
    private readonly Label _title=new() { AutoSize=false,Dock=DockStyle.Top,Height=25,ForeColor=Color.White };
    private readonly Label _status=new() { AutoSize=false,Dock=DockStyle.Fill,ForeColor=Color.Gainsboro };
    private readonly FlowLayoutPanel _buttons=new() { Dock=DockStyle.Bottom,Height=34,WrapContents=false };
    private readonly Button _start,_explore,_back,_next,_skip,_recenter;
    internal event Action? StartRequested,ExploreRequested,BackRequested,NextRequested,SkipRequested,RecenterRequested;
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
        ClientSize=new Size(560,108);
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
        _recenter=Add("&Recenter",()=>RecenterRequested?.Invoke());
        _skip=Add("S&kip tour",()=>SkipRequested?.Invoke());
    }
    internal void Offer(bool resume,string? error)
    {
        IsOffer=true;_title.Text=resume?"Continue your canvas tour?":"Explore your canvas with a short tour";
        _status.Text=error??"Practise the controls at your own pace, or explore the notes yourself.";
        _start.Text=resume?"&Resume tour":"&Start tour";
        _start.Visible=_explore.Visible=true;
        _back.Visible=_next.Visible=_skip.Visible=_recenter.Visible=false;
        AcceptButton=_start;
    }
    internal void UpdateTour(TutorialTour tour,string? error)
    {
        IsOffer=false;
        string[] names=["Move around","Change your view","Your windows live here","Leave yourself a thought","Make it your workspace"];
        _title.Text=$"{tour.Progress.Step+1} / {TutorialTour.StepCount}  ·  {names[tour.Progress.Step]}";
        _status.Text=error??(tour.Travelling?"Moving to the next note. Any navigation input stops the movement."
            :tour.Practised?"✓ You've got it. Continue when you're ready."
            :tour.Step==TutorialStep.Projects?"Shift-drag creates a project area. P opens the project navigator. Esc returns to your desktop."
            :"Try the controls on the note, or choose ‘I understand — continue’. ");
        _start.Visible=_explore.Visible=false;
        _back.Visible=_next.Visible=_skip.Visible=_recenter.Visible=true;
        _back.Enabled=!tour.Travelling && tour.Progress.Step>0;
        _next.Enabled=!tour.Travelling;
        _next.Text=tour.Step==TutorialStep.Projects?"&Finish":tour.Practised?"&Next":"I &understand — continue";
        _recenter.Enabled=!tour.Travelling;
        AcceptButton=_next;
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing) { _title.Font.Dispose();_status.Font.Dispose(); }
        base.Dispose(disposing);
    }
}
