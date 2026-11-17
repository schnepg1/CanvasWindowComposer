using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CanvasDesktop.Tests;

internal sealed class MockAgentSessionManager : IAgentSessionManager
{
    public IReadOnlyList<AgentProfile> SupportedAgents => HerdrSessions.SupportedAgents;
    public AgentProfile DefaultAgent => HerdrSessions.DefaultAgent;
    public AgentProfile FindAgent(string? id) => HerdrSessions.FindAgent(id);

    public List<(Guid Card, string Prompt, string? Folder)> SentPrompts { get; } = [];
    public ConcurrentQueue<(Guid Card, string Prompt, string? Folder, AgentProfile? Agent)> SentPromptRequests { get; } = new();
    public List<(Guid Card, AgentProfile Agent, string Folder)> StartedAgents { get; } = [];
    public List<Guid> StoppedAgents { get; } = [];

    public Task<HerdrSessions.Terminal?> Find(Guid card, CancellationToken cancellation) =>
        Task.FromResult<HerdrSessions.Terminal?>(null);

    public Task<HerdrSessions.Terminal> StartAgent(AgentProfile agent, string folder, Guid card, CancellationToken cancellation, string? prompt = null)
    {
        StartedAgents.Add((card, agent, folder));
        return Task.FromResult(new HerdrSessions.Terminal("t0", "live", "idle", folder));
    }

    public Task<HerdrSessions.Terminal> StartCodex(string folder, Guid card, CancellationToken cancellation, string? prompt = null) =>
        StartAgent(DefaultAgent, folder, card, cancellation, prompt);

    public Task StopAgent(Guid card, CancellationToken cancellation)
    {
        StoppedAgents.Add(card);
        return Task.CompletedTask;
    }

    public Task SendPrompt(Guid card, string prompt, string? folder, CancellationToken cancellation, AgentProfile? agent = null)
    {
        SentPrompts.Add((card, prompt, folder));
        SentPromptRequests.Enqueue((card, prompt, folder, agent));
        return Task.CompletedTask;
    }

    public Task<Dictionary<Guid, HerdrSessions.Terminal>> Snapshot(CancellationToken cancellation) =>
        Task.FromResult(new Dictionary<Guid, HerdrSessions.Terminal>());

    public Task Open(Guid card, string expectedAddress, bool observe, CancellationToken cancellation) =>
        Task.CompletedTask;
}
