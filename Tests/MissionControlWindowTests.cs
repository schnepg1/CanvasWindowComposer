using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CanvasDesktop;
using CanvasDesktop.CanvasView;
using Xunit;

namespace CanvasDesktop.Tests;

public class MissionControlWindowTests
{
    private sealed class MockAgentSessionManager : IAgentSessionManager
    {
        public IReadOnlyList<AgentProfile> SupportedAgents => HerdrSessions.SupportedAgents;
        public AgentProfile DefaultAgent => HerdrSessions.DefaultAgent;
        public AgentProfile FindAgent(string? id) => HerdrSessions.FindAgent(id);

        public Task<HerdrSessions.Terminal?> Find(Guid card, CancellationToken cancellation) =>
            Task.FromResult<HerdrSessions.Terminal?>(null);

        public Task<HerdrSessions.Terminal> StartAgent(AgentProfile agent, string folder, Guid card, CancellationToken cancellation, string? prompt = null) =>
            Task.FromResult(new HerdrSessions.Terminal("t0", "live", "idle", folder));

        public Task<HerdrSessions.Terminal> StartCodex(string folder, Guid card, CancellationToken cancellation, string? prompt = null) =>
            StartAgent(DefaultAgent, folder, card, cancellation, prompt);

        public Task SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation) =>
            Task.CompletedTask;

        public Task<Dictionary<Guid, HerdrSessions.Terminal>> Snapshot(CancellationToken cancellation) =>
            Task.FromResult(new Dictionary<Guid, HerdrSessions.Terminal>());

        public Task Open(Guid card, string expectedAddress, bool observe, CancellationToken cancellation) =>
            Task.CompletedTask;
    }

    [Fact]
    public void MissionControlWindowInitializesWithDarkFluentStyling()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var herdr = new MockAgentSessionManager();
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
            var herdr = new MockAgentSessionManager();
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
                var firstItem = Assert.IsType<ComboBoxItem>(window.AgentSelector.SelectedItem);
                var firstAgent = Assert.IsType<AgentProfile>(firstItem.Tag);
                Assert.Equal("Codex", firstAgent.DisplayName);
                Assert.Equal("Start Codex", window.LaunchButton.Content);

                // Switch to Claude Code (index 1)
                window.AgentSelector.SelectedIndex = 1;
                var claude = HerdrSessions.SupportedAgents.First(a => a.Id == "claude");
                Assert.Equal("Start Claude Code", window.LaunchButton.Content);
                var expectedClaudeColor = (Color)ColorConverter.ConvertFromString(claude.AccentColorHex);
                Assert.Equal(expectedClaudeColor, ((SolidColorBrush)window.LaunchButton.Background).Color);

                // Switch to Gemini CLI (index 2)
                window.AgentSelector.SelectedIndex = 2;
                var gemini = HerdrSessions.SupportedAgents.First(a => a.Id == "gemini");
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
            var herdr = new MockAgentSessionManager();
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
