using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasDesktop;

/// <summary>
/// Domain exception thrown when Herdr process management, communication, or terminal streams fail.
/// </summary>
internal class HerdrException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Abstraction for managing background AI agent terminal sessions via Herdr.
/// Decouples the UI layer from concrete OS process spawning and CLI execution.
/// </summary>
internal interface IAgentSessionManager
{
    IReadOnlyList<AgentProfile> SupportedAgents { get; }
    AgentProfile DefaultAgent { get; }
    AgentProfile FindAgent(string? id);

    Task<HerdrSessions.Terminal> StartAgent(AgentProfile agent, string folder, Guid card, CancellationToken cancellation, string? prompt = null);
    Task<HerdrSessions.Terminal> StartCodex(string folder, Guid card, CancellationToken cancellation, string? prompt = null);
    Task StopAgent(Guid card, CancellationToken cancellation);
    Task SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation);
    Task<Dictionary<Guid, HerdrSessions.Terminal>> Snapshot(CancellationToken cancellation);
    Task<HerdrSessions.Terminal?> Find(Guid card, CancellationToken cancellation);
    Task Open(Guid card, string expectedAddress, bool observe, CancellationToken cancellation);
}
