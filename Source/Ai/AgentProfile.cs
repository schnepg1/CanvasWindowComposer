using System;
using System.Collections.Generic;

namespace CanvasDesktop;

/// <summary>
/// Declarative specification for an AI agent platform supported by Canvas and Herdr.
/// </summary>
internal sealed record AgentProfile(
    string Id,
    string DisplayName,
    string Subtitle,
    string[] CandidateExecutables,
    string[] DefaultArgs,
    string AccentColorHex
);
