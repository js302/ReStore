using ReStore.Services;

namespace ReStore.Tests;

public class CrashReportingTests
{
    private static readonly DateTime T0 = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Format_IncludesHeaderAndFullException()
    {
        var ex = new InvalidOperationException("boom", new IOException("inner"));
        var text = CrashReportFormatter.Format("Unhandled UI exception", ex, T0, "1.2.3", "Saving");

        Assert.Contains("2026-01-02 03:04:05", text);
        Assert.Contains("Source: Unhandled UI exception", text);
        Assert.Contains("Action: Saving", text);
        Assert.Contains("Version: 1.2.3", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("inner", text);
    }

    [Fact]
    public void Format_RedactsSecretsInTheExceptionText()
    {
        var ex = new InvalidOperationException("failed with secretAccessKey=SuperSecret123 at https://user:hunter2@host/x");
        var text = CrashReportFormatter.Format("src", ex, T0);

        Assert.DoesNotContain("SuperSecret123", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void Format_NullException_DoesNotThrow()
    {
        Assert.Contains("Unknown error", CrashReportFormatter.Format("x", null, T0));
    }

    [Fact]
    public void CrashFileName_IsSortableAndFilesystemSafe()
    {
        var name = CrashReportFormatter.CrashFileName(T0);
        Assert.Equal("crash-20260102-030405.txt", name);
        Assert.DoesNotContain(':', name);
    }

    [Fact]
    public void FriendlyMessage_HasNoStackTraceAndOnlyFirstLine()
    {
        var ex = new InvalidOperationException("first line\nsecond line");
        string msg;
        try { throw ex; } catch (Exception e) { msg = CrashReportFormatter.FriendlyMessage("Saving settings", e); }

        Assert.Contains("Saving settings failed.", msg);
        Assert.Contains("first line", msg);
        Assert.DoesNotContain("second line", msg);
        Assert.DoesNotContain(" at ", msg);
        Assert.Contains("Copy details", msg);
    }

    [Fact]
    public void FriendlyMessage_UsesBaseExceptionMessage()
    {
        var ex = new InvalidOperationException("outer", new IOException("disk full"));
        Assert.Contains("disk full", CrashReportFormatter.FriendlyMessage(null, ex));
    }

    [Fact]
    public void Gate_SuppressesSameErrorWithinWindow_AllowsAfter()
    {
        var gate = new ErrorDialogGate(TimeSpan.FromSeconds(60), 10);
        Assert.True(gate.ShouldShow("a", T0));
        Assert.False(gate.ShouldShow("a", T0.AddSeconds(5)));
        Assert.True(gate.ShouldShow("b", T0.AddSeconds(5)));
        Assert.True(gate.ShouldShow("a", T0.AddSeconds(61)));
    }

    [Fact]
    public void Gate_CapsDistinctErrorsPerWindow()
    {
        var gate = new ErrorDialogGate(TimeSpan.FromSeconds(60), 2);
        Assert.True(gate.ShouldShow("a", T0));
        Assert.True(gate.ShouldShow("b", T0));
        Assert.False(gate.ShouldShow("c", T0.AddSeconds(1)));
        Assert.True(gate.ShouldShow("c", T0.AddSeconds(61)));
    }

    [Fact]
    public void Gate_SuppressedAttemptsDoNotExtendTheWindow()
    {
        var gate = new ErrorDialogGate(TimeSpan.FromSeconds(60), 10);
        Assert.True(gate.ShouldShow("a", T0));
        for (var i = 1; i < 60; i++) Assert.False(gate.ShouldShow("a", T0.AddSeconds(i)));
        Assert.True(gate.ShouldShow("a", T0.AddSeconds(60)));
    }

    [Fact]
    public void DedupeKey_SameTypeAndMessage_Matches()
    {
        Assert.Equal(CrashReportFormatter.DedupeKey(new IOException("x")), CrashReportFormatter.DedupeKey(new IOException("x")));
        Assert.NotEqual(CrashReportFormatter.DedupeKey(new IOException("x")), CrashReportFormatter.DedupeKey(new IOException("y")));
    }

    [Fact]
    public void FailureStreak_TripsAtLimit_SuccessResets()
    {
        var s = new FailureStreak(3);
        Assert.False(s.RecordFailure());
        Assert.False(s.RecordFailure());
        s.RecordSuccess();
        Assert.False(s.RecordFailure());
        Assert.False(s.RecordFailure());
        Assert.True(s.RecordFailure());
    }
}
