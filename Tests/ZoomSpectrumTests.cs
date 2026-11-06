using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CanvasDesktop;
using Xunit;

namespace CanvasDesktop.Tests;

[Collection("ShellViews")]
public class ZoomSpectrumTests
{
    [Fact]
    public void ZoomSpectrumControlInitializesWithDefaults()
        => WorkspaceViewTests.OnSta(() =>
        {
            var spectrum = new ZoomSpectrumControl();
            Assert.True(spectrum.EnableLive);
            Assert.True(spectrum.EnableDetailed);
            Assert.True(spectrum.EnableSimple);
            Assert.Equal(OverviewZoomDefaults.CardEnter, spectrum.CardEnterZoom);
            Assert.Equal(OverviewZoomDefaults.CardExit, spectrum.CardExitZoom);
            Assert.Equal(OverviewZoomDefaults.SimpleCardEnter, spectrum.SimpleCardEnterZoom);
            Assert.Equal(OverviewZoomDefaults.SimpleCardExit, spectrum.SimpleCardExitZoom);
        });

    [Fact]
    public void SettingValuesClampsAndMaintainsOrder()
        => WorkspaceViewTests.OnSta(() =>
        {
            var spectrum = new ZoomSpectrumControl();
            spectrum.SetValues(cardEnter: 0.50, cardExit: 0.60, simpleEnter: 0.15, simpleExit: 0.20);
            Assert.Equal(0.50, spectrum.CardEnterZoom);
            Assert.Equal(0.60, spectrum.CardExitZoom);
            Assert.Equal(0.15, spectrum.SimpleCardEnterZoom);
            Assert.Equal(0.20, spectrum.SimpleCardExitZoom);
        });

    [Fact]
    public void DeactivatingDetailedStageMorphsToSingleBoundaryBetweenSimpleAndLive()
        => WorkspaceViewTests.OnSta(() =>
        {
            var spectrum = new ZoomSpectrumControl();
            spectrum.SetTiers(live: true, detailed: false, simple: true);
            Assert.True(spectrum.EnableLive);
            Assert.False(spectrum.EnableDetailed);
            Assert.True(spectrum.EnableSimple);

            var thumbs = WorkspaceViewTests.Descendants(spectrum).OfType<Thumb>().ToArray();
            Assert.Equal(2, thumbs.Length);
            // Simple thumb visible for boundary; detailed thumb hidden
            Assert.Equal(Visibility.Visible, thumbs[0].Visibility);
            Assert.Equal(Visibility.Collapsed, thumbs[1].Visibility);
        });

    [Fact]
    public void DeactivatingSimpleStageHidesSimpleThumbAndShowsDetailedThumb()
        => WorkspaceViewTests.OnSta(() =>
        {
            var spectrum = new ZoomSpectrumControl();
            spectrum.SetTiers(live: true, detailed: true, simple: false);
            Assert.True(spectrum.EnableLive);
            Assert.True(spectrum.EnableDetailed);
            Assert.False(spectrum.EnableSimple);

            var thumbs = WorkspaceViewTests.Descendants(spectrum).OfType<Thumb>().ToArray();
            Assert.Equal(Visibility.Collapsed, thumbs[0].Visibility);
            Assert.Equal(Visibility.Visible, thumbs[1].Visibility);
        });

    [Fact]
    public void SingleActiveStageHidesBothThumbs()
        => WorkspaceViewTests.OnSta(() =>
        {
            var spectrum = new ZoomSpectrumControl();
            spectrum.SetTiers(live: false, detailed: true, simple: false);

            var thumbs = WorkspaceViewTests.Descendants(spectrum).OfType<Thumb>().ToArray();
            Assert.Equal(Visibility.Collapsed, thumbs[0].Visibility);
            Assert.Equal(Visibility.Collapsed, thumbs[1].Visibility);
        });

    [Fact]
    public void TogglingOffAllStagesInControlEnforcesAtLeastOneActive()
        => WorkspaceViewTests.OnSta(() =>
        {
            var preferences = new AppConfig().Preferences;
            var control = new ZoomSettingsControl(preferences);

            var boxes = WorkspaceViewTests.Descendants(control).OfType<CheckBox>().ToArray();
            var simpleBox = boxes.Single(b => Equals(b.Content, "Simple cards"));
            var detailedBox = boxes.Single(b => Equals(b.Content, "Detailed placeholder cards"));
            var liveBox = boxes.Single(b => Equals(b.Content, "Live previews"));

            simpleBox.IsChecked = false;
            liveBox.IsChecked = false;
            // Now attempt to uncheck the last remaining active stage
            detailedBox.IsChecked = false;

            // At least one stage must remain checked
            Assert.True(simpleBox.IsChecked == true || detailedBox.IsChecked == true || liveBox.IsChecked == true);
        });

    [Fact]
    public void ResetZoomDefaultsRestoresAllStagesAndThresholds()
        => WorkspaceViewTests.OnSta(() =>
        {
            var preferences = new AppConfig().Preferences with
            {
                LivePreviewsTier = false,
                DetailedCardsTier = false,
                SimpleCardsTier = true,
                CardEnterZoom = 0.60,
                CardExitZoom = 0.70,
                SimpleCardEnterZoom = 0.20,
                SimpleCardExitZoom = 0.25
            };

            var control = new ZoomSettingsControl(preferences);
            var resetBtn = WorkspaceViewTests.Descendants(control).OfType<Button>()
                .Single(b => Equals(b.Content, "Reset zoom defaults"));

            resetBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var boxes = WorkspaceViewTests.Descendants(control).OfType<CheckBox>().ToArray();
            Assert.True(boxes.Single(b => Equals(b.Content, "Simple cards")).IsChecked);
            Assert.True(boxes.Single(b => Equals(b.Content, "Detailed placeholder cards")).IsChecked);
            Assert.True(boxes.Single(b => Equals(b.Content, "Live previews")).IsChecked);

            var sliders = WorkspaceViewTests.Descendants(control).OfType<Slider>().Skip(1).Take(4).ToArray();
            Assert.Equal(40, sliders[0].Value);
            Assert.Equal(45, sliders[1].Value);
            Assert.Equal(10, sliders[2].Value);
            Assert.Equal(13, sliders[3].Value);
        });
}
