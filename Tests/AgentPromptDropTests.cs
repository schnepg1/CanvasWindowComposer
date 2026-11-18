using CanvasDesktop.CanvasView;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using Xunit;

namespace CanvasDesktop.Tests;

public class AgentPromptDropTests
{
    [Fact]
    public void CanvasAnnotation_WithAgentPrompt_SerializesAndRoundtripsCorrectly()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(340, 212), Text: "Agent Coder", AgentTaskId: "task-42", AgentRole: "coder")
            with { AgentPrompt = "Refactor database access layer" };

        var missionCard = new MissionAgentCard(card);
        Assert.True(MissionAgentCard.IsCard(card));
        Assert.Equal("Refactor database access layer", missionCard.Prompt);

        string json = JsonSerializer.Serialize(card);
        var restored = JsonSerializer.Deserialize<CanvasAnnotation>(json);

        Assert.NotNull(restored);
        Assert.Equal(card.Id, restored.Id);
        Assert.Equal("Refactor database access layer", restored.AgentPrompt);
        Assert.Equal(card, restored);
    }

    [Fact]
    public void CanvasAnnotation_RejectsOverlyLongAgentPrompt()
    {
        var desktop = Guid.NewGuid();
        var card = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
            new(100, 100), new(340, 212), Text: "Agent Coder", AgentTaskId: "task-42", AgentRole: "coder")
            with { AgentPrompt = new string('A', AnnotationDocument.MaximumTextLength + 1) };

        var ex = Assert.Throws<ArgumentException>(() => AnnotationDocument.Validate([card]));
        Assert.Contains("Invalid canvas annotation", ex.Message);
    }

    [Fact]
    public void AgentPromptConfirmationDialog_PropertiesAndKeyInteractions()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var dialog = new AgentPromptConfirmationDialog(IntPtr.Zero, "Architect", "Design multi-agent protocol");

            Assert.Equal("Architect", dialog.AgentName);
            Assert.Equal("Design multi-agent protocol", dialog.PromptText);
            Assert.True(dialog.Topmost);

            // Verify prompt text can be edited
            dialog.PromptText = "Design multi-agent protocol v2";
            Assert.Equal("Design multi-agent protocol v2", dialog.PromptText);

            // Simulate pressing Enter to confirm
            var enterArgs = new KeyEventArgs(Keyboard.PrimaryDevice, new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero), 0, Key.Enter)
            {
                RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent
            };
            dialog.RaiseEvent(enterArgs);

            Assert.True(enterArgs.Handled);
            Assert.True(dialog.Confirmed);
            Assert.Equal("Design multi-agent protocol v2", dialog.PromptText);

            // Re-instantiate for Escape test
            var dialog2 = new AgentPromptConfirmationDialog(IntPtr.Zero, "Programmer", "Fix memory leak");
            dialog2.PromptText = "Edited text that is cancelled";
            var escapeArgs = new KeyEventArgs(Keyboard.PrimaryDevice, new System.Windows.Interop.HwndSource(0, 0, 0, 0, 0, "", IntPtr.Zero), 0, Key.Escape)
            {
                RoutedEvent = System.Windows.UIElement.PreviewKeyDownEvent
            };
            dialog2.RaiseEvent(escapeArgs);

            Assert.True(escapeArgs.Handled);
            Assert.False(dialog2.Confirmed);
        });
    }

    [Fact]
    public void AgentPromptConfirmationDialog_UsesExplicitHighContrastDarkPaletteAndStyles()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var dialog = new AgentPromptConfirmationDialog(IntPtr.Zero, "Gemini CLI", "Audit scrollbars");
            try
            {
                var windowBg = Assert.IsType<System.Windows.Media.SolidColorBrush>(dialog.Background);
                Assert.Equal(MissionControlTheme.WindowBackground.Color, windowBg.Color);

                var promptBg = Assert.IsType<System.Windows.Media.SolidColorBrush>(dialog.PromptBox.Background);
                var promptFg = Assert.IsType<System.Windows.Media.SolidColorBrush>(dialog.PromptBox.Foreground);
                var promptBorder = Assert.IsType<System.Windows.Media.SolidColorBrush>(dialog.PromptBox.BorderBrush);
                Assert.Equal(MissionControlTheme.CardBackground.Color, promptBg.Color);
                Assert.Equal(MissionControlTheme.TextPrimary.Color, promptFg.Color);
                Assert.Equal(MissionControlTheme.CardBorder.Color, promptBorder.Color);

                var focusedBg = Assert.IsType<System.Windows.Media.SolidColorBrush>(dialog.TryFindResource("TextControlBackgroundFocused"));
                var focusedBorder = Assert.IsType<System.Windows.Media.SolidColorBrush>(dialog.TryFindResource("TextControlBorderBrushFocused"));
                Assert.Equal(System.Windows.Media.Color.FromRgb(0x16, 0x20, 0x32), focusedBg.Color);
                Assert.Equal(MissionControlTheme.AccentBlue.Color, focusedBorder.Color);

                Assert.Same(dialog.FindResource("PromptPrimaryButton"), dialog.ConfirmButton.Style);
                Assert.Same(dialog.FindResource("PromptSecondaryButton"), dialog.CancelButton.Style);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void TryPromptAgentWithTextCard_WhenConfirmed_SetsAgentPromptOnCard()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var agentCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(340, 212), Text: "Scout Agent", AgentTaskId: "task-100", AgentRole: "scout");
            var textCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(500, 500), new(740, 548), Text: "Analyze repo performance");

            string folder = Path.Combine(Path.GetTempPath(), "test-drop-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var overview = CreateTestOverview(desktop, folder, [agentCard, textCard]);
                var missionCard = new MissionAgentCard(agentCard);
                string? capturedName = null;
                string? capturedPrompt = null;

                overview.PromptConfirmationDialog = (owner, name, prompt) =>
                {
                    capturedName = name;
                    capturedPrompt = prompt;
                    return (true, prompt); // Confirmed via Enter
                };

                bool handled = overview.TryPromptAgentWithTextCard(textCard, missionCard);

                Assert.True(handled);
                Assert.Equal("Scout Agent", capturedName);
                Assert.Equal("Analyze repo performance", capturedPrompt);

                var updatedAgent = overview.Annotations!.Items.First(a => a.Id == agentCard.Id);
                Assert.Equal("Analyze repo performance", updatedAgent.AgentPrompt);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void TryPromptAgentWithTextCard_WhenEditedInDialog_SetsEditedPromptOnCard()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var agentCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(340, 212), Text: "Lead Agent", AgentTaskId: "task-105", AgentRole: "lead");
            var textCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(500, 500), new(740, 548), Text: "Implement cache");

            string folder = Path.Combine(Path.GetTempPath(), "test-drop-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var overview = CreateTestOverview(desktop, folder, [agentCard, textCard]);
                var missionCard = new MissionAgentCard(agentCard);

                // User edited prompt in dialog before hitting Enter
                overview.PromptConfirmationDialog = (owner, name, prompt) =>
                    (true, prompt + " and add unit tests");

                bool handled = overview.TryPromptAgentWithTextCard(textCard, missionCard);

                Assert.True(handled);
                var updatedAgent = overview.Annotations!.Items.First(a => a.Id == agentCard.Id);
                Assert.Equal("Implement cache and add unit tests", updatedAgent.AgentPrompt);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void TryPromptAgentWithTextCard_WhenCancelled_LeavesAgentPromptUnchanged()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var agentCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(340, 212), Text: "Coder Agent", AgentTaskId: "task-101", AgentRole: "coder");
            var textCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(500, 500), new(740, 548), Text: "Refactor architecture");

            string folder = Path.Combine(Path.GetTempPath(), "test-drop-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var overview = CreateTestOverview(desktop, folder, [agentCard, textCard]);
                var missionCard = new MissionAgentCard(agentCard);

                overview.PromptConfirmationDialog = (owner, name, prompt) => (false, prompt); // Cancelled

                bool handled = overview.TryPromptAgentWithTextCard(textCard, missionCard);

                Assert.True(handled);
                var currentAgent = overview.Annotations!.Items.First(a => a.Id == agentCard.Id);
                Assert.Null(currentAgent.AgentPrompt);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void MoveSelection_DroppingTextCardOntoAgentCard_TriggersPromptAndPreservesTextCardPosition()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var agentCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(340, 212), Text: "Worker Agent", AgentTaskId: "task-102", AgentRole: "worker");
            var textCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(500, 500), new(740, 548), Text: "Run full security audit");

            string folder = Path.Combine(Path.GetTempPath(), "test-drop-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var overview = CreateTestOverview(desktop, folder, [agentCard, textCard]);
                bool dialogShown = false;
                overview.PromptConfirmationDialog = (owner, name, prompt) =>
                {
                    dialogShown = true;
                    Assert.Equal("Worker Agent", name);
                    Assert.Equal("Run full security audit", prompt);
                    return (true, prompt); // Confirmed
                };

                // Calculate translation to drop textCard on top of agentCard
                // textCard is at (500, 500). Move by (-350, -350) -> arrives at (150, 150), which is inside (100, 100, 340, 212).
                double dx = -350;
                double dy = -350;
                var dropPoint = new CanvasPoint(200, 150);

                var item = new CanvasSelectionItem(new(CanvasItemKind.Annotation, textCard.Id), textCard.Bounds, textCard.Text, Annotation: textCard);
                var move = new CanvasSelectionMove([item], dx, dy, dropPoint);

                overview.MoveSelection(move);

                Assert.True(dialogShown);

                // Agent card received the prompt
                var updatedAgent = overview.Annotations!.Items.First(a => a.Id == agentCard.Id);
                Assert.Equal("Run full security audit", updatedAgent.AgentPrompt);

                // Text card position was preserved at (500, 500) rather than moving over agentCard
                var currentTextCard = overview.Annotations!.Items.First(a => a.Id == textCard.Id);
                Assert.Equal(500, currentTextCard.Bounds.X);
                Assert.Equal(500, currentTextCard.Bounds.Y);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    [Fact]
    public void TryPromptAgentWithTextCard_PassesCardsAgentProfileToSendPrompt()
    {
        WorkspaceViewTests.OnSta(() =>
        {
            var desktop = Guid.NewGuid();
            var agentCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(100, 100), new(340, 212), Text: "Gemini CLI", AgentTaskId: "task-103", AgentRole: "gemini");
            var textCard = new CanvasAnnotation(Guid.NewGuid(), desktop, AnnotationKind.Text,
                new(500, 500), new(740, 548), Text: "Check turbo permissions");

            string folder = Path.Combine(Path.GetTempPath(), "test-drop-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var overview = CreateTestOverview(desktop, folder, [agentCard, textCard]);
                var mockHerdr = (MockAgentSessionManager)overview.HerdrSessions;
                overview.PromptConfirmationDialog = (_, _, prompt) => (true, prompt);

                bool handled = overview.TryPromptAgentWithTextCard(textCard, new MissionAgentCard(agentCard));

                Assert.True(handled);
                Assert.True(System.Threading.SpinWait.SpinUntil(() => !mockHerdr.SentPromptRequests.IsEmpty, TimeSpan.FromSeconds(5)));
                Assert.True(mockHerdr.SentPromptRequests.TryDequeue(out var sent));
                Assert.Equal(agentCard.Id, sent.Card);
                Assert.Equal("Check turbo permissions", sent.Prompt);
                Assert.NotNull(sent.Agent);
                Assert.Equal("gemini", sent.Agent!.Id);
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        });
    }

    private static OverviewManager CreateTestOverview(Guid desktop, string folder, CanvasAnnotation[] items)
    {
        Directory.CreateDirectory(folder);
        var canvas = new Canvas();
        var api = new FakeWindowApi();
        var input = new FakeInputRouter();
        var config = new FakeAppConfig { EnableStartupIntro = false };
        var desktops = new FakeVirtualDesktops { CurrentDesktopId = desktop };
        var wm = new WindowManager(canvas, api, config, input, new FakeClock());
        var overview = new OverviewManager(canvas, wm, api, input, config);
        overview.HerdrSessions = new MockAgentSessionManager();
        overview.AttachAnnotations(desktops, Path.Combine(folder, "annotations.json"));
        overview.TransitionTo(OverviewMode.Zooming);

        foreach (var item in items)
        {
            overview.Annotations!.Put(item);
        }

        return overview;
    }
}
