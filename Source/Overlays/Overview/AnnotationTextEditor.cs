using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfTextBox = System.Windows.Controls.TextBox;
using Color = System.Drawing.Color;
using Size = System.Drawing.Size;
using Button = System.Windows.Forms.Button;
using CheckBox = System.Windows.Forms.CheckBox;
using ComboBox = System.Windows.Forms.ComboBox;
using Label = System.Windows.Forms.Label;
using Panel = System.Windows.Forms.Panel;
using HorizontalAlignment = System.Windows.Forms.HorizontalAlignment;
using Point = System.Drawing.Point;

namespace CanvasDesktop;

/// <summary>A focused card preview; editing keeps the same geometry and typography as the canvas.</summary>
internal sealed class AnnotationTextEditor : Form
{
    public WpfTextBox Editor { get; }
    public event Action<bool>? Finished;
    private readonly ElementHost _host;
    private readonly NumericUpDown _size;
    private readonly Label _count;
    private readonly ComboBox _align;
    private readonly ComboBox _fill;
    private readonly CheckBox _bold;
    private readonly CheckBox _italic;
    private readonly CardArtwork _artwork;
    private CanvasAnnotation _item;
    private bool _updating;

    /// <summary>Text size in canvas units; changes preview immediately and are committed on save.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double FontSize
    {
        get => Editor.FontSize;
        set => SetFontSize(value);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AnnotationTextAlign TextAlign
    {
        get => _item.TextAlign;
        set => SetTextAlign(value);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AnnotationTextEmphasis Emphasis
    {
        get => _item.Emphasis;
        set => SetEmphasis(value);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AnnotationCardFill CardFill
    {
        get => _item.CardFill;
        set => SetCardFill(value);
    }

    public void SetFontSize(double fontSize)
    {
        fontSize = double.IsFinite(fontSize)
            ? Math.Clamp(Math.Round(fontSize), AnnotationDocument.MinimumFontSize, AnnotationDocument.MaximumFontSize)
            : AnnotationDocument.DefaultFontSize;
        Editor.FontSize = fontSize;
        _item = _item with { FontSize = fontSize };
        if (_size.Value != (decimal)fontSize) _size.Value = (decimal)fontSize;
    }

    public void SetTextAlign(AnnotationTextAlign align)
    {
        if (!Enum.IsDefined(align)) throw new ArgumentException("Invalid text alignment.");
        _item = _item with { TextAlign = align };
        Editor.TextAlignment = AnnotationVisual.TextAlignmentFor(align);
        _updating = true;
        _align.SelectedIndex = (int)align;
        _updating = false;
    }

    public void SetCardFill(AnnotationCardFill fill)
    {
        if (!Enum.IsDefined(fill)) throw new ArgumentException("Invalid card fill.");
        _item = _item with { CardFill = fill };
        _updating = true;
        _fill.SelectedIndex = (int)fill;
        _updating = false;
        _artwork.InvalidateVisual();
    }

    public void SetEmphasis(AnnotationTextEmphasis emphasis)
    {
        if (!Enum.IsDefined(emphasis)) throw new ArgumentException("Invalid text emphasis.");
        _item = _item with { Emphasis = emphasis };
        bool bold = emphasis is AnnotationTextEmphasis.Bold or AnnotationTextEmphasis.BoldItalic;
        bool italic = emphasis is AnnotationTextEmphasis.Italic or AnnotationTextEmphasis.BoldItalic;
        Editor.FontWeight = bold ? FontWeights.Bold : FontWeights.Normal;
        Editor.FontStyle = italic ? FontStyles.Italic : FontStyles.Normal;
        _updating = true;
        _bold.Checked = bold;
        _italic.Checked = italic;
        _updating = false;
        _artwork.InvalidateVisual();
    }

    public void ToggleBold()
    {
        bool bold = Emphasis is AnnotationTextEmphasis.Bold or AnnotationTextEmphasis.BoldItalic;
        bool italic = Emphasis is AnnotationTextEmphasis.Italic or AnnotationTextEmphasis.BoldItalic;
        SetEmphasis((bold, italic) switch
        {
            (true, true) => AnnotationTextEmphasis.Italic,
            (true, false) => AnnotationTextEmphasis.Normal,
            (false, true) => AnnotationTextEmphasis.BoldItalic,
            _ => AnnotationTextEmphasis.Bold
        });
    }

    public void ToggleItalic()
    {
        bool bold = Emphasis is AnnotationTextEmphasis.Bold or AnnotationTextEmphasis.BoldItalic;
        bool italic = Emphasis is AnnotationTextEmphasis.Italic or AnnotationTextEmphasis.BoldItalic;
        SetEmphasis((bold, italic) switch
        {
            (true, true) => AnnotationTextEmphasis.Bold,
            (true, false) => AnnotationTextEmphasis.BoldItalic,
            (false, true) => AnnotationTextEmphasis.Normal,
            _ => AnnotationTextEmphasis.Italic
        });
    }

    public AnnotationTextEditor(CanvasAnnotation item)
    {
        _item = item;
        Text = item.Kind == AnnotationKind.Text ? "Canvas note" : "Shape label";
        ShowInTaskbar=false; TopMost=true; KeyPreview=true;
        StartPosition=FormStartPosition.Manual; FormBorderStyle=FormBorderStyle.Sizable;
        MaximizeBox=false; MinimizeBox=false; AutoScaleMode=AutoScaleMode.Dpi;
        BackColor=Color.FromArgb(15,20,31); ForeColor=Color.White;
        MinimumSize=new Size(660,460); Size=new Size(760,600); Padding=new Padding(16,12,16,12);

        double initialSize = double.IsFinite(item.FontSize)
            ? Math.Clamp(item.FontSize, AnnotationDocument.MinimumFontSize, AnnotationDocument.MaximumFontSize)
            : AnnotationDocument.DefaultFontSize;

        Editor=new WpfTextBox
        {
            Text=item.Text, AcceptsReturn=true, AcceptsTab=true, TextWrapping=TextWrapping.Wrap,
            MaxLength=AnnotationDocument.MaximumTextLength,
            FontFamily=AnnotationVisual.Typeface.FontFamily, FontSize=initialSize,
            TextAlignment=AnnotationVisual.TextAlignmentFor(item.TextAlign),
            FontWeight=item.Emphasis is AnnotationTextEmphasis.Bold or AnnotationTextEmphasis.BoldItalic ? FontWeights.Bold : FontWeights.Normal,
            FontStyle=item.Emphasis is AnnotationTextEmphasis.Italic or AnnotationTextEmphasis.BoldItalic ? FontStyles.Italic : FontStyles.Normal,
            Foreground=new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(item.Color)),
            Background=System.Windows.Media.Brushes.Transparent, BorderThickness=new Thickness(0),
            Padding=new Thickness(0), Margin=new Thickness(0), MinWidth=0, MinHeight=0,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            FocusVisualStyle=null, FlowDirection=System.Windows.FlowDirection.LeftToRight
        };
        System.Windows.Automation.AutomationProperties.SetName(Editor,"Note text");
        // Remove the theme's textbox chrome/content margins so text aligns with the saved card.
        var content=new FrameworkElementFactory(typeof(ScrollViewer));
        content.Name="PART_ContentHost";
        content.SetValue(ScrollViewer.FocusableProperty,false);
        content.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
        content.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);
        Editor.Template=new ControlTemplate(typeof(WpfTextBox)) { VisualTree=content };
        var card=new System.Windows.Controls.Canvas { Width=item.Bounds.Width,Height=item.Bounds.Height };
        _artwork=new CardArtwork(()=>_item) { Width=card.Width,Height=card.Height };
        card.Children.Add(_artwork);
        var textBounds=AnnotationVisual.TextBounds(item);
        Editor.Width=textBounds.Width; Editor.Height=textBounds.Height;
        System.Windows.Controls.Canvas.SetLeft(Editor,textBounds.X);
        System.Windows.Controls.Canvas.SetTop(Editor,textBounds.Y);
        card.Children.Add(Editor);
        _host=new ElementHost
        {
            Dock=DockStyle.Fill, BackColor=BackColor,
            Child=new Border { Padding=new Thickness(20), Child=new Viewbox { Stretch=Stretch.Uniform,Child=card } }
        };
        Controls.Add(_host);

        var header=new Panel
        {
            Dock=DockStyle.Top, Height=132, BackColor=Color.FromArgb(26,33,48)
        };
        Controls.Add(header);

        var title=new Label
        {
            Text=item.Kind == AnnotationKind.Text ? "Edit note" : "Edit shape label",
            AutoSize=true, Location=new Point(14,8), ForeColor=Color.White,
            Font=new Font("Segoe UI",13,System.Drawing.FontStyle.Bold)
        };
        var subtitle=new Label
        {
            Text="Text, wrapping and style preview on the card, then save together.",
            AutoSize=true, Location=new Point(14,31), ForeColor=Color.FromArgb(161,174,195),
            Font=new Font("Segoe UI",9)
        };
        var sizeLabel=new Label
        {
            Text="Text size", Location=new Point(14,61), Size=new Size(64,26), TextAlign=ContentAlignment.MiddleLeft,
            ForeColor=Color.FromArgb(226,232,240), Font=new Font("Segoe UI",9)
        };
        _size=new NumericUpDown
        {
            Location=new Point(80,59), Size=new Size(64,26), Minimum=8, Maximum=(decimal)AnnotationDocument.MaximumFontSize,
            DecimalPlaces=0, Increment=1, Value=(decimal)initialSize, TextAlign=HorizontalAlignment.Right,
            BackColor=Color.FromArgb(35,43,58), ForeColor=Color.White, BorderStyle=BorderStyle.FixedSingle,
            Font=new Font("Segoe UI",10), AccessibleName="Text size"
        };
        _size.ValueChanged+=(_,_)=>SetFontSize((double)_size.Value);
        var sizeUnit=new Label
        {
            Text="px", AutoSize=true, Location=new Point(148,63), ForeColor=Color.FromArgb(161,174,195),
            Font=new Font("Segoe UI",9)
        };
        _count=new Label
        {
            AutoSize=false, Size=new Size(160,26), TextAlign=ContentAlignment.MiddleRight,
            ForeColor=Color.FromArgb(161,174,195), Font=new Font("Segoe UI",9)
        };
        header.Controls.Add(title); header.Controls.Add(subtitle);
        header.Controls.Add(sizeLabel); header.Controls.Add(_size); header.Controls.Add(sizeUnit);
        header.Controls.Add(_count);
        header.Resize+=(_,_)=>_count.Left=Math.Max(14,header.ClientSize.Width-_count.Width-14);
        _count.Left=Math.Max(14,header.ClientSize.Width-_count.Width-14);

        int linkLeft=176;
        foreach (var preset in new[] { ("S", 14), ("M", 24), ("L", 40), ("XL", 72), ("2XL", 120), ("3XL", 200) })
        {
            var link=new LinkLabel
            {
                Text=preset.Item1, AutoSize=true, Location=new Point(linkLeft,62),
                LinkColor=Color.FromArgb(147,197,253), ActiveLinkColor=Color.White,
                VisitedLinkColor=Color.FromArgb(147,197,253), LinkBehavior=LinkBehavior.HoverUnderline,
                Font=new Font("Segoe UI",9,System.Drawing.FontStyle.Bold), TabStop=false,
                AccessibleName=$"Text size {preset.Item2}"
            };
            link.LinkClicked+=(_,_)=>SetFontSize(preset.Item2);
            header.Controls.Add(link);
            linkLeft+=preset.Item1.Length*10+10;
        }

        var styleLabel=new Label
        {
            Text="Style", Location=new Point(14,101), Size=new Size(38,24), TextAlign=ContentAlignment.MiddleLeft,
            ForeColor=Color.FromArgb(226,232,240), Font=new Font("Segoe UI",9)
        };
        _bold=new CheckBox
        {
            Text="Bold", AutoSize=true, Location=new Point(56,100), ForeColor=Color.White,
            BackColor=Color.FromArgb(26,33,48), Font=new Font("Segoe UI",9), AccessibleName="Bold"
        };
        _italic=new CheckBox
        {
            Text="Italic", AutoSize=true, Location=new Point(120,100), ForeColor=Color.White,
            BackColor=Color.FromArgb(26,33,48), Font=new Font("Segoe UI",9), AccessibleName="Italic"
        };
        _bold.Checked=item.Emphasis is AnnotationTextEmphasis.Bold or AnnotationTextEmphasis.BoldItalic;
        _italic.Checked=item.Emphasis is AnnotationTextEmphasis.Italic or AnnotationTextEmphasis.BoldItalic;
        _bold.CheckedChanged+=(_,_)=> { if(!_updating) ToggleBold(); };
        _italic.CheckedChanged+=(_,_)=> { if(!_updating) ToggleItalic(); };

        var alignLabel=new Label
        {
            Text="Align", Location=new Point(196,101), Size=new Size(38,24), TextAlign=ContentAlignment.MiddleLeft,
            ForeColor=Color.FromArgb(226,232,240), Font=new Font("Segoe UI",9)
        };
        _align=new ComboBox
        {
            Location=new Point(238,97), Size=new Size(92,26), DropDownStyle=ComboBoxStyle.DropDownList,
            FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(35,43,58), ForeColor=Color.White,
            Font=new Font("Segoe UI",9), AccessibleName="Text alignment"
        };
        _align.Items.AddRange(["Left","Center","Right"]);
        _align.SelectedIndex=(int)item.TextAlign;
        _align.SelectedIndexChanged+=(_,_)=> { if(!_updating) SetTextAlign((AnnotationTextAlign)_align.SelectedIndex); };

        var fillLabel=new Label
        {
            Text="Card", Location=new Point(346,101), Size=new Size(36,24), TextAlign=ContentAlignment.MiddleLeft,
            ForeColor=Color.FromArgb(226,232,240), Font=new Font("Segoe UI",9)
        };
        _fill=new ComboBox
        {
            Location=new Point(386,97), Size=new Size(96,26), DropDownStyle=ComboBoxStyle.DropDownList,
            FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(35,43,58), ForeColor=Color.White,
            Font=new Font("Segoe UI",9), AccessibleName="Card fill",
            Enabled=item.Kind == AnnotationKind.Text
        };
        _fill.Items.AddRange(["Solid","Tinted","Outline"]);
        _fill.SelectedIndex=(int)item.CardFill;
        _fill.SelectedIndexChanged+=(_,_)=> { if(!_updating) SetCardFill((AnnotationCardFill)_fill.SelectedIndex); };

        header.Controls.Add(styleLabel); header.Controls.Add(_bold); header.Controls.Add(_italic);
        header.Controls.Add(alignLabel); header.Controls.Add(_align);
        header.Controls.Add(fillLabel); header.Controls.Add(_fill);

        var hint=new Label
        {
            Text="Ctrl+Enter saves · Esc cancels · Ctrl+B/I style · Ctrl+L/E/R align · Ctrl +/− size", Dock=DockStyle.Bottom, Height=26,
            TextAlign=ContentAlignment.MiddleLeft, ForeColor=Color.FromArgb(161,174,195), Font=new Font("Segoe UI",9)
        };
        var save=new Button
        {
            Text="Save note", Dock=DockStyle.Bottom, Height=42, FlatStyle=FlatStyle.Flat,
            BackColor=Color.FromArgb(59,130,246), ForeColor=Color.White,
            Font=new Font("Segoe UI",10,System.Drawing.FontStyle.Bold), AccessibleName="Save note"
        };
        save.FlatAppearance.BorderSize=0;
        save.Click+=(_,_)=>Finished?.Invoke(true);
        Controls.Add(hint); Controls.Add(save);

        void UpdateCount()=>_count.Text=$"{Editor.Text.Length:N0} / {AnnotationDocument.MaximumTextLength:N0}";
        Editor.TextChanged+=(_,_)=>UpdateCount();
        UpdateCount();

        Editor.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape || (e.Key==Key.Enter && Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)))
            { e.Handled=true; Finished?.Invoke(e.Key==Key.Enter); }
            else if(Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
            {
                if(e.Key is Key.OemPlus or Key.Add) { e.Handled=true; SetFontSize(FontSize+1); }
                else if(e.Key is Key.OemMinus or Key.Subtract) { e.Handled=true; SetFontSize(FontSize-1); }
                else if(e.Key is Key.D0 or Key.NumPad0) { e.Handled=true; SetFontSize(AnnotationDocument.DefaultFontSize); }
                else if(e.Key==Key.B) { e.Handled=true; ToggleBold(); }
                else if(e.Key==Key.I) { e.Handled=true; ToggleItalic(); }
                else if(e.Key==Key.L) { e.Handled=true; SetTextAlign(AnnotationTextAlign.Left); }
                else if(e.Key==Key.E) { e.Handled=true; SetTextAlign(AnnotationTextAlign.Center); }
                else if(e.Key==Key.R) { e.Handled=true; SetTextAlign(AnnotationTextAlign.Right); }
            }
        };
        KeyDown+=(_,e)=>
        {
            if(e.KeyCode==Keys.Escape || (e.Control && e.KeyCode==Keys.Enter))
            { e.SuppressKeyPress=true; e.Handled=true; Finished?.Invoke(e.KeyCode==Keys.Enter); }
        };
        FormClosing+=(_,e)=> { if(e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Finished?.Invoke(false); } };
        Shown+=(_,_)=> { Editor.Focus(); Editor.CaretIndex=Editor.Text.Length; };
    }

    // ElementHost owns the WPF subtree and is disposed with the form.
    private sealed class CardArtwork(Func<CanvasAnnotation> item) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
            => AnnotationVisual.Draw(dc,item(),VisualTreeHelper.GetDpi(this).PixelsPerDip,drawText:false);
    }
}
