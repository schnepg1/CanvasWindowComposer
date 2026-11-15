using System;
using System.Collections.Generic;
using System.Linq;

namespace CanvasDesktop;

internal enum CanvasLayerTier
{
    CanvasSurface = 0,
    Zones = 10,
    BackpackAndDrawers = 20,
    DockAndToolbars = 30,
    PeekPreview = 40,
    ContextPopups = 50,
    ModalDialogs = 60
}

internal sealed class CanvasLayerCoordinator
{
    private sealed record LayerRegistration(
        CanvasLayerTier Tier,
        string Name,
        Action RaiseWithoutActivation,
        Func<bool>? IsAlive,
        long Sequence);

    private readonly object _syncLock = new();
    private readonly Dictionary<string, LayerRegistration> _registrations = new(StringComparer.Ordinal);
    private long _sequenceCounter;

    public void Register(CanvasLayerTier tier, string name, Action raiseWithoutActivation, Func<bool>? isAlive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(raiseWithoutActivation);

        lock (_syncLock)
        {
            _registrations[name] = new LayerRegistration(
                tier,
                name,
                raiseWithoutActivation,
                isAlive,
                unchecked(++_sequenceCounter));
        }
    }

    public void Unregister(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        lock (_syncLock)
        {
            _registrations.Remove(name);
        }
    }

    public void RaiseAll()
    {
        LayerRegistration[] targets;
        lock (_syncLock)
        {
            targets = _registrations.Values
                .OrderBy(r => (int)r.Tier)
                .ThenBy(r => r.Sequence)
                .ToArray();
        }

        RaiseTargets(targets);
    }

    public void RaiseAbove(CanvasLayerTier tier)
    {
        LayerRegistration[] targets;
        lock (_syncLock)
        {
            targets = _registrations.Values
                .Where(r => r.Tier > tier)
                .OrderBy(r => (int)r.Tier)
                .ThenBy(r => r.Sequence)
                .ToArray();
        }

        RaiseTargets(targets);
    }

    internal int Count
    {
        get
        {
            lock (_syncLock) return _registrations.Count;
        }
    }

    internal bool IsRegistered(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        lock (_syncLock) return _registrations.ContainsKey(name);
    }

    private static void RaiseTargets(LayerRegistration[] targets)
    {
        foreach (var target in targets)
        {
            if (CheckAlive(target.IsAlive))
            {
                try
                {
                    target.RaiseWithoutActivation();
                }
                catch (ObjectDisposedException)
                {
                    // Ignore already-disposed layers during asynchronous tear down.
                }
            }
        }
    }

    private static bool CheckAlive(Func<bool>? isAlive)
    {
        if (isAlive == null) return true;
        try
        {
            return isAlive();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
