using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace CanvasDesktop;

internal sealed class AnnotationToolbar : Form
{
    private readonly Dictionary<AnnotationMode,ToolStripButton> _modes=[];
    private readonly ToolStripLabel _status=new();
    private readonly ToolStripDropDownButton _nearby=new("Nearby");
    private readonly NumericUpDown _radius=new() { Minimum=1,Maximum=100000,Increment=100,Value=1200,Width=100,AccessibleName="Selection radius in canvas units" };
    public event Action<double>? NearbyRequested;
    public event Action<AnnotationMode>? ModeRequested;
    public event Action<string>? ColorRequested;
    public event Action<double>? StrokeRequested;
    public event Action<double>? FontSizeRequested;
    public event Action<AnnotationTextAlign>? TextAlignRequested;
    public event Action<AnnotationTextEmphasis>? EmphasisRequested;
    public event Action<AnnotationCardFill>? CardFillRequested;
    public event Action? UndoRequested,RedoRequested,EditRequested,DeleteRequested,RetrySaveRequested,ComposerRequested;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=0x80; return cp; } }
    public AnnotationToolbar()
    {
        FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true; KeyPreview=true;
        AutoScaleMode=AutoScaleMode.Dpi; AutoSize=true; AutoSizeMode=AutoSizeMode.GrowAndShrink;
        StartPosition=FormStartPosition.Manual; BackColor=Color.FromArgb(28,34,46); Padding=new Padding(6);
        var bar=new ToolStrip { Dock=DockStyle.None,GripStyle=ToolStripGripStyle.Hidden,BackColor=BackColor,ForeColor=Color.White,AutoSize=true };
        Controls.Add(bar);
        foreach(var mode in Enum.GetValues<AnnotationMode>())
        {
            var button=new ToolStripButton(mode==AnnotationMode.Navigate?"Windows":mode.ToString()) { DisplayStyle=ToolStripItemDisplayStyle.Text };
            button.Click+=(_,_)=>ModeRequested?.Invoke(mode); _modes.Add(mode,button); bar.Items.Add(button);
        }
        bar.Items.Add(new ToolStripSeparator());
        var colors=new ToolStripDropDownButton("Color");
        foreach(var pair in new[]{("Gold","#FBBF24"),("White","#FFFFFF"),("Coral","#FB7185"),("Sky","#60A5FA"),("Mint","#34D399"),("Lilac","#C084FC")})
            colors.DropDownItems.Add(pair.Item1,null,(_,_)=>ColorRequested?.Invoke(pair.Item2));
        bar.Items.Add(colors);
        var widths=new ToolStripDropDownButton("Stroke");
        foreach(var width in new[]{1d,3d,6d}) widths.DropDownItems.Add(width.ToString(),null,(_,_)=>StrokeRequested?.Invoke(width));
        bar.Items.Add(widths);
        var sizes=new ToolStripDropDownButton("Text size");
        foreach(var size in new[]{12d,18d,24d,32d,40d,56d,72d})
            sizes.DropDownItems.Add(size.ToString("0"),null,(_,_)=>FontSizeRequested?.Invoke(size));
        bar.Items.Add(sizes);
        var textStyle=new ToolStripDropDownButton("Text");
        void Style(string title,Action action) => textStyle.DropDownItems.Add(title,null,(_,_)=>action());
        Style("Plain",()=>EmphasisRequested?.Invoke(AnnotationTextEmphasis.Normal));
        Style("Bold",()=>EmphasisRequested?.Invoke(AnnotationTextEmphasis.Bold));
        Style("Italic",()=>EmphasisRequested?.Invoke(AnnotationTextEmphasis.Italic));
        Style("Bold italic",()=>EmphasisRequested?.Invoke(AnnotationTextEmphasis.BoldItalic));
        textStyle.DropDownItems.Add(new ToolStripSeparator());
        Style("Align left",()=>TextAlignRequested?.Invoke(AnnotationTextAlign.Left));
        Style("Align center",()=>TextAlignRequested?.Invoke(AnnotationTextAlign.Center));
        Style("Align right",()=>TextAlignRequested?.Invoke(AnnotationTextAlign.Right));
        textStyle.DropDownItems.Add(new ToolStripSeparator());
        Style("Solid card",()=>CardFillRequested?.Invoke(AnnotationCardFill.Solid));
        Style("Tinted card",()=>CardFillRequested?.Invoke(AnnotationCardFill.Tinted));
        Style("Outline card",()=>CardFillRequested?.Invoke(AnnotationCardFill.None));
        bar.Items.Add(textStyle);
        void Button(string title,Action action) => bar.Items.Add(title,null,(_,_)=>action());
        Button("Undo",()=>UndoRequested?.Invoke()); Button("Redo",()=>RedoRequested?.Invoke()); Button("Edit",()=>EditRequested?.Invoke()); Button("Delete",()=>DeleteRequested?.Invoke());
        Button("Save",()=>RetrySaveRequested?.Invoke());
        Button("AI Compose",()=>ComposerRequested?.Invoke());
        var radiusHost=new ToolStripControlHost(_radius);
        _nearby.DropDownItems.Add(new ToolStripLabel("Radius in canvas units"));
        _nearby.DropDownItems.Add(radiusHost);
        _nearby.DropDownItems.Add("Select around camera",null,(_,_)=>NearbyRequested?.Invoke((double)_radius.Value));
        bar.Items.Add(_nearby);
        bar.Items.Add(_status);
    }
    public void SetSelectionRadius(double radius) => _radius.Value=(decimal)Math.Clamp(radius,1,100000);
    public void UpdateState(AnnotationMode mode,string? status,int selectedCount=0)
    {
        foreach(var pair in _modes) pair.Value.Checked=pair.Key==mode;
        _status.Text=status is null?$"{selectedCount} selected":"Warning"; _status.ToolTipText=status??"";
    }
}
