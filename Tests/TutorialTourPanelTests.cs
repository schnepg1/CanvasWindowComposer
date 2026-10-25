using CanvasDesktop;
using CanvasDesktop.CanvasView;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class TutorialTourPanelTests
{
    [Fact]
    public void ScreenControlsStayInsidePanelAndKeyboardAcceptButtonMatchesTheAction()
        =>WorkspaceViewTests.OnSta(()=>
        {
            using var panel=new TutorialTourPanel();
            panel.Offer(false,null);panel.Show();panel.PerformLayout();
            Assert.True(panel.IsOffer);Assert.Equal("&Start tour",((Button)panel.AcceptButton!).Text);
            var tour=new TutorialTour(new(Guid.Empty));tour.Start();panel.UpdateTour(tour,null);panel.PerformLayout();
            Assert.False(panel.IsOffer);Assert.Equal("&Next",((Button)panel.AcceptButton!).Text);
            var buttons=panel.Controls.OfType<FlowLayoutPanel>().Single();
            tour.ObservePan(64);panel.UpdateTour(tour,null);Assert.Contains("got it",panel.StatusText);
            Assert.Equal("&Next",((Button)panel.AcceptButton!).Text);
            bool controlsRequested=false;
            panel.ControlsRequested+=()=>controlsRequested=true;
            buttons.Controls.OfType<Button>().Single(b=>b.Text=="&Controls").PerformClick();
            Assert.True(controlsRequested);
            tour=new TutorialTour(new(Guid.Empty));tour.Start();
            for(int step=0;step<TutorialTour.StepCount;step++)
            {
                panel.UpdateTour(tour,null);panel.PerformLayout();
                foreach(var b in buttons.Controls.OfType<Button>().Where(b=>b.Visible))
                    Assert.True(b.Right<=buttons.ClientSize.Width,$"Clipped button: {b.Text}");
                Assert.Equal("&Back",buttons.Controls[0].Text);
                Assert.Same(panel.AcceptButton,buttons.Controls[4]);
                var status=panel.Controls.OfType<Label>().Single(l=>l.Dock==DockStyle.Fill);
                var textSize=TextRenderer.MeasureText(status.Text,status.Font,new Size(status.Width,int.MaxValue),TextFormatFlags.WordBreak);
                Assert.True(textSize.Height<=status.Height,$"Clipped prompt: {status.Text}");
                Assert.Equal(step==TutorialTour.StepCount-1?"&Finish":"&Next",((Button)panel.AcceptButton!).Text);
                if(step<TutorialTour.StepCount-1)tour.Next();
            }
            panel.UpdateTour(tour,null,overviewHotkey:false);
            Assert.DoesNotContain("Alt+Q",panel.StatusText);
            if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                Directory.CreateDirectory(Path.Combine(workspace,"TestResults"));
                using var bitmap=new Bitmap(panel.Width,panel.Height);panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));
                bitmap.Save(Path.Combine(workspace,"TestResults","guided-tutorial-controls.png"),ImageFormat.Png);
            }
            panel.Hide();
        });
}
