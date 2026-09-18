using Madorin.AI.Runtime.Server.ProviderWorkers;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderWorkerCrashFuseTests
{
    [TestMethod]
    public void ThirdCrashAtExactThirtySecondBoundary_Fuses()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var fuse = new ProviderWorkerCrashFuse(clock);

        Assert.IsFalse(fuse.RecordCrash());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.IsFalse(fuse.RecordCrash());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.IsTrue(fuse.RecordCrash());
        Assert.IsTrue(fuse.IsFused);
        Assert.IsTrue(fuse.RecordCrash());
    }

    [TestMethod]
    public void ThirdCrashJustOutsideWindow_DoesNotFuse()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var fuse = new ProviderWorkerCrashFuse(clock);

        Assert.IsFalse(fuse.RecordCrash());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.IsFalse(fuse.RecordCrash());
        clock.Advance(TimeSpan.FromSeconds(15) + TimeSpan.FromTicks(1));
        Assert.IsFalse(fuse.RecordCrash());
        Assert.IsFalse(fuse.IsFused);
    }

    [TestMethod]
    public void TwoCrashes_RemainBelowThreshold()
    {
        var fuse = new ProviderWorkerCrashFuse();
        Assert.IsFalse(fuse.RecordCrash());
        Assert.IsFalse(fuse.RecordCrash());
        Assert.IsFalse(fuse.IsFused);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
