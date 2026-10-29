using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class AnnotationTextReviewRegressionTests
{
    private static CanvasAnnotation Note() => new(Guid.NewGuid(),Guid.NewGuid(),AnnotationKind.Text,
        new(100,100),new(340,148),Text:"A title");

    private static T Named<T>(DependencyObject root,string name) where T : FrameworkElement
    {
        if (root is T element && System.Windows.Automation.AutomationProperties.GetName(element)==name) return element;
        for (int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            try { return Named<T>(VisualTreeHelper.GetChild(root,i),name); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Missing control: {name}");
    }

    [Fact]
    public void DoneCommitsPendingSizeBeforePublishingTheItem()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var editor=new AnnotationTextEditor(Note());
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            Named<ComboBox>(editor.FormattingToolbar,"Text size").Text="137";
            CanvasAnnotation? saved=null;
            editor.Finished+=save=> { if (save) saved=editor.CurrentItem; };
            Assert.True(editor.Complete(true));
            Assert.Equal(137,saved!.FontSize);
            Assert.True(saved.Bounds.Height>=AnnotationTextLayout.Layout(saved,saved.Bounds.Width-16)+16);
        });

    [Theory]
    [InlineData("")]
    [InlineData("not a size")]
    [InlineData("NaN")]
    [InlineData("99999")]
    public void InvalidPendingSizeKeepsTheDraftEditableAndCanBeCorrected(string value)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var editor=new AnnotationTextEditor(Note());
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            var size=Named<ComboBox>(editor.FormattingToolbar,"Text size");
            size.Text=value;
            int completed=0;
            editor.Finished+=_=>completed++;
            Assert.False(editor.Complete(true));
            Assert.Equal(0,completed);
            Assert.Equal("A title",editor.Editor.Text);
            size.Text="72";
            Assert.True(editor.Complete(true));
            Assert.Equal(1,completed); Assert.Equal(72,editor.FontSize);
        });

    [Fact]
    public void RejectedSaveCanBeRetriedWithoutLosingTextOrRoundingTheFont()
        => WorkspaceViewTests.OnSta(() =>
        {
            var note=Note() with { FontSize=23.456789 };
            using var editor=new AnnotationTextEditor(note);
            int attempts=0;
            editor.Finished+=save=>
            {
                if (save && ++attempts==1) editor.RejectCompletion("The annotation limit was reached.");
            };
            editor.Editor.Text="Keep this draft";
            Assert.False(editor.Complete(true));
            Assert.Equal("Keep this draft",editor.Editor.Text);
            Assert.Equal(note.FontSize,editor.FontSize);
            Assert.True(editor.Complete(true));
            Assert.Equal(2,attempts);
        });

    [Fact]
    public void NativeCloseDefersDisposalUntilTheClosingEventHasReturned()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var editor=new AnnotationTextEditor(Note());
            int completions=0;
            editor.Finished+=save=>
            {
                Assert.False(save); completions++; editor.Dispose();
            };
            editor.Show();
            editor.Close();
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>{});
            Assert.Equal(1,completions);
            Assert.False(editor.IsVisible);
        });

    [Theory]
    [InlineData("W")]
    [InlineData("\U0001F338")]
    [InlineData("W\u0301")]
    public void LargeFontChangesGrowTheCardWideEnoughForAnUnbreakableCharacter(string text)
        => WorkspaceViewTests.OnSta(() =>
        {
            using var editor=new AnnotationTextEditor(Note() with { Text=text });
            editor.FontSize=400;
            editor.Show(); System.Windows.Forms.Application.DoEvents();
            var item=editor.CurrentItem;
            var metrics=AnnotationTextLayout.Measure(item,item.Bounds.Width-16);
            Assert.True(item.Bounds.Width>240);
            Assert.True(metrics.Width<=item.Bounds.Width-18);
            Assert.True(editor.Editor.GetRectFromCharacterIndex(text.Length).X<=editor.Editor.ActualWidth);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedOffscreenTitlesUseABoundedViewportAndCanScrollBackToTheStart(bool renderBeforeMovingCaret)
        => WorkspaceViewTests.OnSta(() =>
        {
            var item=Note() with
            {
                A=new(-2000,-500), B=new(98000,1000),
                Text=string.Join("\n",Enumerable.Repeat("A very large category heading",40)), FontSize=400
            };
            using var editor=new AnnotationTextEditor(item);
            editor.SetPlacement(new CanvasViewTransform(0,0,1,new(0,0,800,600)),new(0,0,800,600));
            editor.Show();
            if (renderBeforeMovingCaret) System.Windows.Forms.Application.DoEvents();
            var handle=(Windows.Win32.Foundation.HWND)new System.Windows.Interop.WindowInteropHelper(editor).Handle;
            Assert.True(Windows.Win32.PInvoke.GetWindowRect(handle,out var bounds));
            Assert.InRange(bounds.left,0,800); Assert.InRange(bounds.top,0,600);
            Assert.InRange(bounds.right-bounds.left,1,800); Assert.InRange(bounds.bottom-bounds.top,1,600);
            Assert.Equal(100000,editor.CurrentItem.Bounds.Width);
            editor.Editor.CaretIndex=0;
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>{});
            Assert.Equal(0,editor.Editor.CaretIndex);
            var caret=editor.Editor.PointToScreen(editor.Editor.GetRectFromCharacterIndex(0).TopLeft);
            Assert.InRange(caret.X,(double)bounds.left,bounds.right);
            Assert.InRange(caret.Y,(double)bounds.top,bounds.bottom);
        });

    [Fact]
    public void MovingTheCaretBeforeTheFirstRenderedFrameDoesNotJumpBackToTheEnd()
        => WorkspaceViewTests.OnSta(() =>
        {
            using var editor = new AnnotationTextEditor(Note() with { Text = "Keep the requested caret position" });
            editor.Show();
            Assert.Equal(editor.Editor.Text.Length, editor.Editor.CaretIndex);
            editor.Editor.CaretIndex = 0;
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => { });
            Assert.Equal(0, editor.Editor.CaretIndex);
        });

    [Fact]
    public void FullHeightNotesKeepTheEndCaretOutsideTheFormattingToolbar()
        => WorkspaceViewTests.OnSta(() =>
        {
            var item=Note() with { A=new(100,0),B=new(340,48),Text=string.Join("\n",Enumerable.Repeat("A line",80)) };
            using var editor=new AnnotationTextEditor(item);
            editor.SetPlacement(new CanvasViewTransform(0,0,1,new(0,0,1000,600)),new(0,0,1000,600));
            editor.Show();
            editor.Editor.CaretIndex=editor.Editor.Text.Length;
            editor.Editor.BringIntoView(editor.Editor.GetRectFromCharacterIndex(editor.Editor.CaretIndex));
            editor.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>{});
            var toolbarHandle=(Windows.Win32.Foundation.HWND)new System.Windows.Interop.WindowInteropHelper(editor.FormattingToolbar).Handle;
            Assert.True(Windows.Win32.PInvoke.GetWindowRect(toolbarHandle,out var toolbar));
            var caret=editor.Editor.GetRectFromCharacterIndex(editor.Editor.CaretIndex);
            var top=editor.Editor.PointToScreen(caret.TopLeft);
            var bottom=editor.Editor.PointToScreen(caret.BottomRight);
            Assert.False(top.X>=toolbar.left && top.X<=toolbar.right && bottom.Y>=toolbar.top && top.Y<=toolbar.bottom);
            Assert.InRange(bottom.Y,0,601);
        });

    [Theory]
    [InlineData("W")]
    [InlineData("f")]
    public void NarrowItalicNotesFitTheTrailingGlyphInk(string text)
        => WorkspaceViewTests.OnSta(() =>
        {
            var item=Note() with { B=new(180,148),Text=text,FontSize=400,Emphasis=AnnotationTextEmphasis.Italic };
            item=AnnotationVisual.FitText(item);
            var drawing=new DrawingGroup();
            using (var context=drawing.Open()) AnnotationTextLayout.Layout(item,item.Bounds.Width-16,context);
            Assert.True(drawing.Bounds.Right<=item.Bounds.Width-16,$"Ink {drawing.Bounds} exceeds fitted width {item.Bounds.Width-16}.");
        });
}
