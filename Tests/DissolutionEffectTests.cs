using CanvasDesktop.CanvasView;

namespace CanvasDesktop.Tests;

public class DissolutionEffectTests
{
    private static readonly CanvasRect Card = new(100, 200, 320, 180);

    [Fact]
    public void SpawnProducesParticlesAcrossTheItemAndAFadingGhost()
    {
        var effect = new DissolutionEffect();
        Assert.True(effect.Spawn(Card, 0xFF3B82F6, now: 1000, particleCount: 60));

        var frame = Assert.Single(effect.Sample(1000));

        Assert.Equal(60, frame.Particles.Count);
        Assert.True(frame.GhostOpacity > 0);
        Assert.All(frame.Particles, p =>
        {
            Assert.InRange(p.X, Card.X - 1, Card.X + Card.Width + 1);
            Assert.InRange(p.Y, Card.Y - 1, Card.Y + Card.Height + 1);
            Assert.InRange(p.Alpha, 0.001, 1);
        });
    }

    [Fact]
    public void ParticlesDriftFadeAndThenExpire()
    {
        var effect = new DissolutionEffect();
        effect.Spawn(Card, 0xFFFBBF24, now: 0, particleCount: 40);

        var start = Assert.Single(effect.Sample(1));
        var middle = Assert.Single(effect.Sample(300));

        Assert.NotEqual(start.Particles[0].X, middle.Particles[0].X);
        Assert.True(middle.Particles.Average(p => p.Alpha) < start.Particles.Average(p => p.Alpha));
        Assert.True(effect.HasParticles(200));
        Assert.Empty(effect.Sample((long)DissolutionEffect.DefaultDurationMilliseconds + 1));
        Assert.False(effect.HasParticles((long)DissolutionEffect.DefaultDurationMilliseconds + 1));
    }

    [Fact]
    public void ExpiredBurstsArePruned()
    {
        var effect = new DissolutionEffect();
        effect.Spawn(Card, 0xFFFBBF24, now: 0, particleCount: 20);
        Assert.Equal(1, effect.ActiveBursts);

        effect.Advance(5000);

        Assert.Equal(0, effect.ActiveBursts);
    }

    [Fact]
    public void SameSpawnOrderProducesIdenticalMotion()
    {
        var first = new DissolutionEffect();
        var second = new DissolutionEffect();
        first.Spawn(Card, 0xFFFBBF24, 0, 24);
        second.Spawn(Card, 0xFFFBBF24, 0, 24);

        Assert.Equal(
            Assert.Single(first.Sample(200)).Particles,
            Assert.Single(second.Sample(200)).Particles);
    }

    [Fact]
    public void GhostCanBeDisabledForConnectors()
    {
        var effect = new DissolutionEffect();
        effect.Spawn(new CanvasRect(0, 0, 100, 2), 0xFFFBBF24, 0, 12, ghost: false);

        Assert.Equal(0, Assert.Single(effect.Sample(1)).GhostOpacity);
    }

    [Fact]
    public void InvalidBoundsAndEmptyCountsAreRejected()
    {
        var effect = new DissolutionEffect();
        Assert.False(effect.Spawn(new CanvasRect(double.NaN, 0, 10, 10), 0xFFFBBF24, 0));
        Assert.False(effect.Spawn(new CanvasRect(0, 0, 10, 10), 0xFFFBBF24, 0, 0));
    }
}
