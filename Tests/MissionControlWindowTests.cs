using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class MissionControlWindowTests
{
    [Fact]
    public void MissionControlWindowInitializesWithDarkFluentStyling()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new HerdrSessions();
            var window = new MissionControlWindow(IntPtr.Zero, herdr);
            try
            {
                Assert.Equal("Mission Control", window.Title);
                Assert.True(window.ExtendsContentIntoTitleBar);
                Assert.NotNull(window.Background);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ShowAgentPopulatesAgentSelectorAndLaunchButtonWithAgentTheming()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new HerdrSessions();
            var window = new MissionControlWindow(IntPtr.Zero, herdr);
            try
            {
                var cardAnnotation = new CanvasAnnotation(Guid.NewGuid(), Guid.NewGuid(), AnnotationKind.Text,
                    new(100, 100), new(340, 212), Text: "Test Agent", AgentTaskId: "task-01", AgentRole: "Programmer");
                var card = new MissionAgentCard(cardAnnotation);

                window.ShowAgent(null, card, null, "C:\\", _ => true, _ => true, () => { });

                Assert.NotNull(window.AgentSelector);
                Assert.NotNull(window.LaunchButton);

                Assert.Equal(HerdrSessions.SupportedAgents.Count, window.AgentSelector.Items.Count);
                Assert.Equal("Codex", ((AgentProfile)window.AgentSelector.SelectedItem).DisplayName);
                Assert.Equal("Start Codex", window.LaunchButton.Content);

                // Switch to Claude Code
                var claude = HerdrSessions.SupportedAgents.First(a => a.Id == "claude");
                window.AgentSelector.SelectedItem = claude;
                Assert.Equal("Start Claude Code", window.LaunchButton.Content);
                var expectedClaudeColor = (Color)ColorConverter.ConvertFromString(claude.AccentColorHex);
                Assert.Equal(expectedClaudeColor, ((SolidColorBrush)window.LaunchButton.Background).Color);

                // Switch to Gemini CLI
                var gemini = HerdrSessions.SupportedAgents.First(a => a.Id == "gemini");
                window.AgentSelector.SelectedItem = gemini;
                Assert.Equal("Start Gemini CLI", window.LaunchButton.Content);
                var expectedGeminiColor = (Color)ColorConverter.ConvertFromString(gemini.AccentColorHex);
                Assert.Equal(expectedGeminiColor, ((SolidColorBrush)window.LaunchButton.Background).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ShowMissionAndShowMissionsRenderFluentCardsWithoutError()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new HerdrSessions();
            var window = new MissionControlWindow(IntPtr.Zero, herdr);
            try
            {
                var zone = new ZoneView(Guid.NewGuid(), "AI Mission Control: Alpha",
                    "#3B82F6", new WorldRect { X = 0, Y = 0, W = 500, H = 500 }) { AgentTaskId = "task-alpha" };

                window.ShowMissions([zone], _ => true, _ => { }, _ => { });

                var cardAnnotation = new CanvasAnnotation(Guid.NewGuid(), Guid.NewGuid(), AnnotationKind.Text,
                    new(100, 100), new(340, 212), Text: "Coder", AgentTaskId: "task-alpha", AgentRole: "Programmer");
                var card = new MissionAgentCard(cardAnnotation);

                window.ShowMission(zone, [card], _ => { }, _ => true, _ => true);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
