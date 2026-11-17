using FluentAssertions;
using ReStore.Services;

namespace ReStore.Tests;

public class BatchOperationSummaryTests
{
    [Fact]
    public void Format_WithoutFailures_IsJustTheCount()
    {
        var summary = new BatchOperationSummary();
        summary.AddSuccess();
        summary.AddSuccess();

        summary.Format("Deleted").Should().Be("Deleted 2 backup(s).");
        summary.HasFailures.Should().BeFalse();
    }

    [Fact]
    public void Format_ListsEachFailureWithItsReason()
    {
        var summary = new BatchOperationSummary();
        summary.AddSuccess();
        summary.AddFailure("Docs (2026-01-01 10:00)", "Access denied");
        summary.AddFailure("Pics (2026-01-02 10:00)", "Network unreachable");

        var text = summary.Format("Deleted");

        text.Should().Contain("Deleted 1 backup(s). 2 failed:");
        text.Should().Contain("Docs (2026-01-01 10:00)").And.Contain("Access denied");
        text.Should().Contain("Pics (2026-01-02 10:00)").And.Contain("Network unreachable");
        summary.HasFailures.Should().BeTrue();
    }

    [Fact]
    public void Format_TruncatesLongFailureLists()
    {
        var summary = new BatchOperationSummary();
        for (var i = 0; i < 15; i++)
        {
            summary.AddFailure($"item{i}", "boom");
        }

        var text = summary.Format("Deleted");

        text.Should().Contain("item9").And.NotContain("item10");
        text.Should().Contain("...and 5 more");
        summary.Failures.Should().HaveCount(15);
    }

    [Fact]
    public void RestoreFolderName_IsDirectoryPlusUtcTimestamp()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var name = BatchOperationSummary.RestoreFolderName("Documents", new DateTime(2026, 3, 9, 14, 5, 59, DateTimeKind.Utc), used);

        name.Should().Be(Path.Combine("Documents", "20260309-140559"));
    }

    [Fact]
    public void RestoreFolderName_SanitisesAndDeduplicates()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ts = new DateTime(2026, 3, 9, 14, 5, 59, DateTimeKind.Utc);

        var first = BatchOperationSummary.RestoreFolderName("My/Docs", ts, used);
        var second = BatchOperationSummary.RestoreFolderName("my/docs", ts, used);

        first.Should().Be(Path.Combine("My_Docs", "20260309-140559"));
        second.Should().Be(Path.Combine("my_docs", "20260309-140559-2"));
        BatchOperationSummary.RestoreFolderName("", ts, used).Should().StartWith("backup");
    }
}
