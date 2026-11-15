using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using CanvasDesktop.CanvasView;

namespace CanvasDesktop;

/// <summary>
/// Builds the in-place context menu for living agent cards on the canvas.
/// </summary>
internal static class AgentCardContextMenuBuilder
{
    public static ContextMenuStrip Build(
        CanvasAnnotation note,
        bool isLive,
        Action<AgentProfile> onSelectProfile,
        Action onToggleAgent,
        Action onSetWorkingDirectory,
        Action onEditPrompt,
        Action onToggleLiveConsole,
        Action onPutInBackpack,
        Action onDeleteAgentCard)
    {
        var menu = new ContextMenuStrip();
        var activeProfile = HerdrSessions.FindAgent(note.AgentRole);

        // 1. Agent Profile with 5 profile submenu items
        var profileMenu = new ToolStripMenuItem("Agent Profile");
        foreach (var agent in HerdrSessions.SupportedAgents)
        {
            string displayName = agent.Id switch
            {
                "codex" => "OpenAI Codex",
                "gemini" => "Google Gemini CLI",
                _ => agent.DisplayName
            };
            var item = new ToolStripMenuItem(displayName)
            {
                Checked = string.Equals(agent.Id, activeProfile.Id, StringComparison.OrdinalIgnoreCase)
            };
            var targetAgent = agent;
            item.Click += (_, _) => onSelectProfile(targetAgent);
            profileMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(profileMenu);

        // 2. Start Agent / Stop Agent (toggles based on whether the agent session is currently live)
        var toggleAgent = new ToolStripMenuItem(isLive ? "Stop Agent" : "Start Agent");
        toggleAgent.Click += (_, _) => onToggleAgent();
        menu.Items.Add(toggleAgent);

        // 3. Set Working Directory…
        var setWorkingDir = new ToolStripMenuItem("Set Working Directory…");
        setWorkingDir.Click += (_, _) => onSetWorkingDirectory();
        menu.Items.Add(setWorkingDir);

        // 4. Edit Prompt…
        var editPrompt = new ToolStripMenuItem("Edit Prompt…");
        editPrompt.Click += (_, _) => onEditPrompt();
        menu.Items.Add(editPrompt);

        // 5. Toggle Live Console
        var toggleConsole = new ToolStripMenuItem("Toggle Live Console");
        toggleConsole.Click += (_, _) => onToggleLiveConsole();
        menu.Items.Add(toggleConsole);

        // Separator
        menu.Items.Add(new ToolStripSeparator());

        // Put in backpack
        var putInBackpack = new ToolStripMenuItem("Put in backpack");
        putInBackpack.Click += (_, _) => onPutInBackpack();
        menu.Items.Add(putInBackpack);

        // Delete agent card
        var deleteCard = new ToolStripMenuItem("Delete agent card");
        deleteCard.Click += (_, _) => onDeleteAgentCard();
        menu.Items.Add(deleteCard);

        return menu;
    }
}
