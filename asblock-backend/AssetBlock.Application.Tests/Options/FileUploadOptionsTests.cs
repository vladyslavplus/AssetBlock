using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Options;

public sealed class FileUploadOptionsTests
{
    private readonly FileUploadOptions _opts = new();

    [Theory]
    [InlineData("archive.zip", ".zip")]
    [InlineData("ARCHIVE.ZIP", ".zip")]
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
    [InlineData("file.7z")]
    [InlineData("file.rar")]
    [InlineData("file.gz")]
    [InlineData("")]
    [InlineData("noext")]
    public void TryMatchAllowedExtension_WhenNotAllowed_ReturnsFalse(string fileName)
    {
        _opts.TryMatchAllowedExtension(fileName, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("archive.zip", "archive.zip")]
    [InlineData("ARCHIVE.ZIP", "ARCHIVE.zip")]
    [InlineData("path/to/MyArchive.TAR.GZ", "MyArchive.tar.gz")]
    [InlineData(@"..\..\..\etc\passwd.zip", "passwd.zip")]
    [InlineData("my archive file.zip", "my_archive_file.zip")]
    [InlineData("hello\r\nworld\t.tar", "hello__world.tar")]
    [InlineData("foo\"bar'baz`qux.tgz", "foobarbazqux.tgz")]
    [InlineData("файл.zip", "asset.zip")]
    [InlineData("..hidden.zip", "hidden.zip")]
    [InlineData("....zip", "asset.zip")]
    [InlineData("", "asset.zip")]
    [InlineData("   ", "asset.zip")]
    [InlineData("<script>alert(1)</script>.zip", "script.zip")]
    public void NormalizeDisplayFileName_ProducesSafeConservativeAsciiFileName(string input, string expected)
    {
        _opts.NormalizeDisplayFileName(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeDisplayFileName_CapsLongBaseName()
    {
        var longName = new string('a', 200) + ".zip";
        var normalized = _opts.NormalizeDisplayFileName(longName);
        normalized.Should().HaveLength(104); // 100 base chars + 4 for .zip
        normalized.Should().EndWith(".zip");
    }

    [Fact]
    public void NormalizeDisplayFileName_WithCustomFallback_UsesFallback()
    {
        var normalized = _opts.NormalizeDisplayFileName("???", fallbackBaseName: "custom_package");
        normalized.Should().Be("custom_package.zip");
    }

    [Fact]
    public void NormalizeDisplayFileName_PreservesSafeMultipartExtension()
    {
        var opts = new FileUploadOptions { AllowedExtensions = [".tar.gz", ".zip"] };
        var normalized = opts.NormalizeDisplayFileName("archive.TAR.GZ");
        normalized.Should().Be("archive.tar.gz");
    }
}
