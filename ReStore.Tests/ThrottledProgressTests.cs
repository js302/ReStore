using FluentAssertions;
using ReStore.Core.src.utils;

namespace ReStore.Tests;

public class ThrottledProgressTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void Report_ShouldForwardFirstReportImmediately()
    {
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(seen.Add, Interval, () => 0);

        progress.Report(1);

        seen.Should().Equal(1);
    }

    [Fact]
    public void Report_ShouldDropReportsInsideTheInterval_AndForwardAfterIt()
    {
        long now = 0;
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(seen.Add, Interval, () => now);

        progress.Report(1);
        now = Interval.Ticks - 1;
        progress.Report(2);
        progress.Report(3);
        now = Interval.Ticks;
        progress.Report(4);

        seen.Should().Equal(1, 4);
    }

    [Fact]
    public void Flush_ShouldDeliverOnlyTheNewestSuppressedReport_Once()
    {
        long now = 0;
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(seen.Add, Interval, () => now);

        progress.Report(1);
        progress.Report(2);
        progress.Report(3);
        progress.Flush();
        progress.Flush();

        seen.Should().Equal(1, 3);
    }

    [Fact]
    public void Flush_WithNothingPending_ShouldDoNothing()
    {
        var seen = new List<int>();
        var progress = new ThrottledProgress<int>(seen.Add, Interval, () => 0);

        progress.Flush();
        progress.Report(1);
        progress.Flush();

        seen.Should().Equal(1);
    }

    [Fact]
    public void Report_ShouldBeLimitedToTenPerSecond_WhenFloodedForOneSecond()
    {
        long now = 0;
        var count = 0;
        var progress = new ThrottledProgress<int>(_ => count++, Interval, () => now);

        for (var ms = 0; ms < 1000; ms++)
        {
            now = TimeSpan.FromMilliseconds(ms).Ticks;
            progress.Report(ms);
        }

        count.Should().Be(10);
    }

    [Fact]
    public void Constructor_ShouldRejectNullSink()
    {
        var act = () => new ThrottledProgress<int>(null!, Interval);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Report_FromManyThreads_ShouldDeliverToTheSinkInAcceptedOrder()
    {
        var counter = 0;
        var seen = new List<int>();
        // Zero interval: every report is forwarded; the clock value doubles as the acceptance sequence.
        var progress = new ThrottledProgress<int>(seen.Add, TimeSpan.Zero, () => Interlocked.Increment(ref counter));

        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 2000; i++)
            {
                progress.Report(Volatile.Read(ref counter) + 1);
            }
        });

        // Under the lock, sink calls are serialised (List.Add would corrupt or lose items otherwise).
        seen.Should().HaveCount(8000);
    }
}
