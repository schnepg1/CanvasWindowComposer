namespace CanvasDesktop.CanvasView;

/// <summary>
/// One dust speck of a dissolving canvas item. Positions stay in world units so
/// camera pans and zooms during the animation carry the dust with the canvas.
/// </summary>
public readonly record struct DissolutionParticleState(
    double X,
    double Y,
    double Size,
    double Alpha);

/// <summary>A ghost of the removed item plus the dust it broke into at one instant.</summary>
public readonly record struct DissolutionFrame(
    CanvasRect Bounds,
    uint Argb,
    double GhostOpacity,
    double Scale,
    IReadOnlyList<DissolutionParticleState> Particles);

/// <summary>
/// Deterministic particle model behind the delete "dissolution" effect. It owns no
/// timers and no UI types: callers spawn at a timestamp and sample at a later one.
/// </summary>
public sealed class DissolutionEffect
{
    /// <summary>Upper bound for any particle, including its stagger delay.</summary>
    public const double DefaultDurationMilliseconds = 900;
    public const int DefaultParticleCount = 80;
    public const int MaximumParticleCount = 240;
    private const int MaximumBursts = 32;
    private const double GhostFadeMilliseconds = 240;
    private const double Gravity = 26; // world units per second squared

    private readonly record struct Particle(
        double X, double Y, double VelocityX, double VelocityY,
        double Size, double Lifetime);

    private sealed class Burst
    {
        public required CanvasRect Bounds { get; init; }
        public required uint Argb { get; init; }
        public required long Spawned { get; init; }
        public required double Duration { get; init; }
        public required bool Ghost { get; init; }
        public required Particle[] Particles { get; init; }
    }

    private readonly List<Burst> _bursts = [];
    private uint _random = 0x9E3779B9;

    public int ActiveBursts => _bursts.Count;

    /// <summary>Starts a burst over the item's world-space bounds.</summary>
    public bool Spawn(CanvasRect bounds, uint argb, long now, int particleCount = DefaultParticleCount, bool ghost = true)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)) return false;
        if (particleCount <= 0) return false;

        double width = Math.Max(2, bounds.Width);
        double height = Math.Max(2, bounds.Height);
        particleCount = Math.Clamp(particleCount, 12, MaximumParticleCount);
        if (argb >> 24 == 0) argb |= 0xFF000000u;

        Advance(now);
        if (_bursts.Count >= MaximumBursts) _bursts.RemoveAt(0);

        var particles = new Particle[particleCount];
        double duration = 0;
        for (int i = 0; i < particleCount; i++)
        {
            // Spread across the whole item so its silhouette itself breaks apart.
            double u = NextUnit();
            double v = NextUnit();
            double x = bounds.X + u * width;
            double y = bounds.Y + v * height;
            double dx = u - 0.5;
            double dy = v - 0.5;
            double jitterX = (NextUnit() - 0.5) * 0.9;
            double jitterY = (NextUnit() - 0.5) * 0.9;
            double angle = Math.Atan2(dy * 0.7 + jitterY, dx + jitterX);
            double speed = 16 + NextUnit() * 58;
            // Inner dust leaves first, the rim trails, so the card appears to erode.
            // Every speck exists from the first frame; only its lifetime is staggered.
            double lifetime = 420 + Math.Sqrt(dx * dx + dy * dy) * 300 + NextUnit() * 240;
            duration = Math.Max(duration, lifetime);
            particles[i] = new Particle(
                x, y,
                Math.Cos(angle) * speed,
                Math.Sin(angle) * speed - (10 + NextUnit() * 24),
                1.4 + NextUnit() * 3.8,
                lifetime);
        }

        _bursts.Add(new Burst
        {
            Bounds = new CanvasRect(bounds.X, bounds.Y, width, height),
            Argb = argb,
            Spawned = now,
            Duration = duration,
            Ghost = ghost,
            Particles = particles
        });
        return true;
    }

    public bool HasParticles(long now)
    {
        Advance(now);
        return _bursts.Count > 0;
    }

    /// <summary>Drops finished bursts so a paused canvas does not keep them alive.</summary>
    public void Advance(long now)
    {
        for (int i = _bursts.Count - 1; i >= 0; i--)
        {
            if (now - _bursts[i].Spawned > _bursts[i].Duration) _bursts.RemoveAt(i);
        }
    }

    public void Clear() => _bursts.Clear();

    /// <summary>Particles visible at <paramref name="now"/>, oldest burst first.</summary>
    public IReadOnlyList<DissolutionFrame> Sample(long now)
    {
        Advance(now);
        if (_bursts.Count == 0) return Array.Empty<DissolutionFrame>();

        var frames = new List<DissolutionFrame>(_bursts.Count);
        foreach (var burst in _bursts)
        {
            long elapsed = now - burst.Spawned;
            if (elapsed < 0) continue;
            var states = new List<DissolutionParticleState>(burst.Particles.Length);
            foreach (var particle in burst.Particles)
            {
                double age = elapsed;
                if (age >= particle.Lifetime) continue;
                double t = age / particle.Lifetime;
                double seconds = age / 1000.0;
                double travel = 1 - Math.Pow(1 - t, 2);
                states.Add(new DissolutionParticleState(
                    particle.X + particle.VelocityX * seconds * travel,
                    particle.Y + particle.VelocityY * seconds * travel + 0.5 * Gravity * seconds * seconds,
                    particle.Size * (1 - 0.7 * t),
                    Math.Pow(1 - t, 1.35)));
            }

            double ghost = burst.Ghost && elapsed < GhostFadeMilliseconds
                ? Math.Pow(1 - elapsed / GhostFadeMilliseconds, 2) * 0.32
                : 0;
            if (states.Count == 0 && ghost <= 0.001) continue;
            frames.Add(new DissolutionFrame(
                burst.Bounds, burst.Argb, ghost,
                1 + 0.05 * Math.Min(1, elapsed / GhostFadeMilliseconds),
                states));
        }
        return frames;
    }

    private double NextUnit()
    {
        // xorshift32: stable across runtimes, so tests can assert exact motion.
        _random ^= _random << 13;
        _random ^= _random >> 17;
        _random ^= _random << 5;
        return (_random & 0xFFFFFF) / 16777216.0;
    }
}
