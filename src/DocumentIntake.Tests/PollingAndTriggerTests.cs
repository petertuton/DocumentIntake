using DocumentIntake.Functions.Orchestrations;
using DocumentIntake.Functions.Triggers;

namespace DocumentIntake.Tests;

public sealed class PollingScheduleTests
{
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(4, 10)]
    [InlineData(6, 30)]
    public void GetDelay_FollowsTheRamp(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PollingSchedule.GetDelay(attempt));
    }

    [Fact]
    public void GetDelay_CapsAtMaxDelayOnceRampIsExhausted()
    {
        Assert.Equal(PollingSchedule.MaxDelay, PollingSchedule.GetDelay(7));
        Assert.Equal(PollingSchedule.MaxDelay, PollingSchedule.GetDelay(500));
    }

    [Fact]
    public void GetDelay_RejectsNegativeAttempts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PollingSchedule.GetDelay(-1));
    }

    [Fact]
    public void EarlyDelaysAreShorterThanLaterOnes()
    {
        for (var i = 1; i < 7; i++)
        {
            Assert.True(PollingSchedule.GetDelay(i) >= PollingSchedule.GetDelay(i - 1));
        }
    }
}

public sealed class BlobCreatedTriggerTests
{
    [Fact]
    public void TryParseBlobUrl_SplitsContainerAndBlob()
    {
        var parsed = BlobCreatedTrigger.TryParseBlobUrl(
            "https://acct.blob.core.windows.net/inbox/folder/invoice 01.pdf",
            out var container,
            out var blob);

        Assert.True(parsed);
        Assert.Equal("inbox", container);
        Assert.Equal("folder/invoice 01.pdf", blob);
    }

    [Fact]
    public void TryParseBlobUrl_DecodesEscapedSegments()
    {
        Assert.True(BlobCreatedTrigger.TryParseBlobUrl(
            "https://acct.blob.core.windows.net/inbox/a%20b%2Bc.pdf",
            out _,
            out var blob));

        Assert.Equal("a b+c.pdf", blob);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("https://acct.blob.core.windows.net/inbox")]
    [InlineData("https://acct.blob.core.windows.net/inbox/")]
    public void TryParseBlobUrl_RejectsUnusableUrls(string? url)
    {
        Assert.False(BlobCreatedTrigger.TryParseBlobUrl(url, out _, out _));
    }

    [Fact]
    public void BuildInstanceId_IsDeterministicAndStorageSafe()
    {
        var first = BlobCreatedTrigger.BuildInstanceId("invoice.pdf", "0x8DC");
        var second = BlobCreatedTrigger.BuildInstanceId("invoice.pdf", "0x8DC");

        Assert.Equal(first, second);
        Assert.StartsWith("di-", first, StringComparison.Ordinal);
        Assert.Equal(35, first.Length);
        Assert.All(first[3..], c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void BuildInstanceId_ChangesWhenTheBlobContentChanges()
    {
        Assert.NotEqual(
            BlobCreatedTrigger.BuildInstanceId("invoice.pdf", "etag-1"),
            BlobCreatedTrigger.BuildInstanceId("invoice.pdf", "etag-2"));

        Assert.NotEqual(
            BlobCreatedTrigger.BuildInstanceId("a.pdf", "etag-1"),
            BlobCreatedTrigger.BuildInstanceId("b.pdf", "etag-1"));
    }

    [Fact]
    public void BuildInstanceId_ToleratesMissingEtag()
    {
        Assert.Equal(
            BlobCreatedTrigger.BuildInstanceId("invoice.pdf", null),
            BlobCreatedTrigger.BuildInstanceId("invoice.pdf", string.Empty));
    }
}
