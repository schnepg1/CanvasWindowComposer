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
            Assert.False(panel.IsOffer);Assert.Contains("understand",((Button)panel.AcceptButton!).Text);
            var buttons=panel.Controls.OfType<FlowLayoutPanel>().Single();
            foreach(var b in buttons.Controls.OfType<Button>().Where(b=>b.Visible))
                Assert.True(b.Right<=buttons.ClientSize.Width,$"Clipped button: {b.Text}");
            tour.ObservePan(64);panel.UpdateTour(tour,null);Assert.Contains("got it",panel.StatusText);
            Assert.Equal("&Next",((Button)panel.AcceptButton!).Text);
            if(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is {} workspace)
            {
                Directory.CreateDirectory(Path.Combine(workspace,"TestResults"));
                using var bitmap=new Bitmap(panel.Width,panel.Height);panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));
                bitmap.Save(Path.Combine(workspace,"TestResults","guided-tutorial-controls.png"),ImageFormat.Png);
            }
            panel.Hide();
        });
}
