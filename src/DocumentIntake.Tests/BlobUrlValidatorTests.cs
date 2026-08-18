using DocumentIntake.Functions.Options;
using DocumentIntake.Functions.Services;

namespace DocumentIntake.Tests;

public sealed class BlobUrlValidatorTests
{
    private static readonly Uri ServiceUri = new("https://stexample.blob.core.windows.net/");

    private static bool TryParse(string? url, out string? container, out string? blobName, out string? error)
    {
        var ok = BlobUrlValidator.TryParse(url, ServiceUri, Containers.PublicViewable, out var reference, out error);
        container = reference?.ContainerName;
        blobName = reference?.BlobName;
        return ok;
    }

    [Theory]
    [InlineData("https://stexample.blob.core.windows.net/completed/form.pdf", "form.pdf")]
    [InlineData("https://STEXAMPLE.blob.core.windows.net/completed/form.pdf", "form.pdf")]
    [InlineData("https://stexample.blob.core.windows.net/completed/2026/03/form%20one.pdf", "2026/03/form one.pdf")]
    [InlineData("https://stexample.blob.core.windows.net/completed/form.pdf?sv=2024-01-01&sig=abc", "form.pdf")]
    public void TryParse_AcceptsBlobsInAllowedContainers(string url, string expectedBlobName)
    {
        Assert.True(TryParse(url, out var container, out var blobName, out var error));
        Assert.Equal(Containers.Completed, container);
        Assert.Equal(expectedBlobName, blobName);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/completed/form.pdf")]
    [InlineData("http://stexample.blob.core.windows.net/completed/form.pdf")]
    [InlineData("https://user:pass@stexample.blob.core.windows.net/completed/form.pdf")]
    [InlineData("https://attacker.example.com/completed/form.pdf")]
    [InlineData("https://stexample.blob.core.windows.net:8443/completed/form.pdf")]
    [InlineData("https://stexample.blob.core.windows.net/inbox/form.pdf")]
    [InlineData("https://stexample.blob.core.windows.net/failed/form.pdf")]
    [InlineData("https://stexample.blob.core.windows.net/completed")]
    [InlineData("https://stexample.blob.core.windows.net/completed/")]
    [InlineData("https://stexample.blob.core.windows.net/completed/../inbox/form.pdf")]
    [InlineData("https://stexample.blob.core.windows.net/completed/%2E%2E/inbox/form.pdf")]
    public void TryParse_RejectsUrlsOutsideThePermittedSurface(string? url)
    {
        Assert.False(TryParse(url, out var container, out var blobName, out var error));
        Assert.Null(container);
        Assert.Null(blobName);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
