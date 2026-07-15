using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using FluentAssertions;

namespace AssetBlock.Application.Tests.Options;

public sealed class FileUploadOptionsTests
{
    private readonly FileUploadOptions _opts = new();

    [Theory]
    [InlineData("archive.zip", ".zip")]
    [InlineData("ARCHIVE.ZIP", ".zip")]
    [InlineData("a.7z", ".7z")]
    [InlineData("a.rar", ".rar")]
    [InlineData("a.tar", ".tar")]
    [InlineData("a.tgz", ".tgz")]
    [InlineData("a.tar.gz", ".tar.gz")]
    [InlineData("path/to/My.TAR.GZ", ".tar.gz")]
    public void TryMatchAllowedExtension_WhenAllowed_ReturnsNormalizedSuffix(string fileName, string expected)
    {
        _opts.TryMatchAllowedExtension(fileName, out var matched).Should().BeTrue();
        matched.Should().Be(expected);
    }

    [Theory]
    [InlineData("file.png")]
    [InlineData("file.gz")]
    [InlineData("")]
    [InlineData("noext")]
    public void TryMatchAllowedExtension_WhenNotAllowed_ReturnsFalse(string fileName)
    {
        _opts.TryMatchAllowedExtension(fileName, out _).Should().BeFalse();
    }
}
