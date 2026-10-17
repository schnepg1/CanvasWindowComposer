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

namespace CanvasDesktop;

/// <summary>A focused card preview; editing keeps the same geometry and typography as the canvas.</summary>
internal sealed class AnnotationTextEditor : Form
{
    public WpfTextBox Editor { get; }
    public event Action<bool>? Finished;
    private readonly ElementHost _host;

    public AnnotationTextEditor(CanvasAnnotation item)
    {
        Text = item.Kind == AnnotationKind.Text ? "Canvas note" : "Shape label";
        ShowInTaskbar=false; TopMost=true; KeyPreview=true;
        StartPosition=FormStartPosition.Manual; FormBorderStyle=FormBorderStyle.Sizable;
        MaximizeBox=false; MinimizeBox=false; AutoScaleMode=AutoScaleMode.Dpi;
        BackColor=Color.FromArgb(15,20,31); ForeColor=Color.White;
        MinimumSize=new Size(380,280); Size=new Size(480,340); Padding=new Padding(16);

        var title=new System.Windows.Forms.Label
        {
            Text="Edit your card", Dock=DockStyle.Top, Height=32,
            ForeColor=Color.White, Font=new Font("Segoe UI",12,System.Drawing.FontStyle.Bold)
        };
        var hint=new System.Windows.Forms.Label
        {
            Text="Ctrl+Enter to save · Esc to cancel", Dock=DockStyle.Bottom, Height=26,
            ForeColor=Color.FromArgb(161,174,195), Font=new Font("Segoe UI",9)
        };
        var save=new Button
        {
            Text="Save note", Dock=DockStyle.Bottom, Height=38, FlatStyle=FlatStyle.Flat,
            BackColor=Color.FromArgb(45,59,80), ForeColor=Color.White, Font=new Font("Segoe UI",10)
        };
        save.FlatAppearance.BorderSize=0;
        save.Click+=(_,_)=>Finished?.Invoke(true);

        Editor=new WpfTextBox
        {
            Text=item.Text, AcceptsReturn=true, AcceptsTab=true, TextWrapping=TextWrapping.Wrap,
            MaxLength=AnnotationDocument.MaximumTextLength,
            FontFamily=AnnotationVisual.Typeface.FontFamily, FontSize=AnnotationVisual.FontSize,
            Foreground=new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(item.Color)),
            Background=System.Windows.Media.Brushes.Transparent, BorderThickness=new Thickness(0),
            Padding=new Thickness(0), Margin=new Thickness(0), MinWidth=0, MinHeight=0,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,
            FocusVisualStyle=null, FlowDirection=System.Windows.FlowDirection.LeftToRight
        };
        // Remove the theme's textbox chrome/content margins so text aligns with the saved card.
        var content=new FrameworkElementFactory(typeof(ScrollViewer));
        content.Name="PART_ContentHost";
        content.SetValue(ScrollViewer.FocusableProperty,false);
        Editor.Template=new ControlTemplate(typeof(WpfTextBox)) { VisualTree=content };
        var card=new System.Windows.Controls.Canvas { Width=item.Bounds.Width,Height=item.Bounds.Height };
        card.Children.Add(new CardArtwork(item) { Width=card.Width,Height=card.Height });
        var textBounds=AnnotationVisual.TextBounds(item);
        Editor.Width=textBounds.Width; Editor.Height=textBounds.Height;
        System.Windows.Controls.Canvas.SetLeft(Editor,textBounds.X);
        System.Windows.Controls.Canvas.SetTop(Editor,textBounds.Y);
        card.Children.Add(Editor);
        _host=new ElementHost
        {
            Dock=DockStyle.Fill, BackColor=BackColor,
            Child=new Border { Padding=new Thickness(16), Child=new Viewbox { Stretch=Stretch.Uniform,Child=card } }
        };
        Controls.Add(_host); Controls.Add(title); Controls.Add(hint); Controls.Add(save);
        Editor.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape || (e.Key==Key.Enter && Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)))
            { e.Handled=true; Finished?.Invoke(e.Key==Key.Enter); }
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
    private sealed class CardArtwork(CanvasAnnotation item) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
            => AnnotationVisual.Draw(dc,item,VisualTreeHelper.GetDpi(this).PixelsPerDip,drawText:false);
    }
}
