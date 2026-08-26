using System.IO.Compression;
using System.Text;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using System.Formats.Tar;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class ArchiveSafetyInspectorTests
{
    private readonly ArchiveSafetyInspector _sut;

    public ArchiveSafetyInspectorTests()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveInspectionOptions
        {
            MaxEntries = 100,
            MaxTotalExpandedBytes = 10 * 1024 * 1024,
            MaxEntryExpandedBytes = 5 * 1024 * 1024,
            MaxCompressionRatio = 100.0,
            MaxPathLength = 256,
            MaxPathDepth = 32,
            MaxReadmeBytes = 16384,
            MaxManifestFiles = 8,
            MaxManifestBytes = 16384
        });

        _sut = new ArchiveSafetyInspector(options, NullLogger<ArchiveSafetyInspector>.Instance);
    }

    [Fact]
    public async Task Inspect_WhenValidZipWithReadmeAndManifest_ShouldReturnSafeResult()
    {
        const string packageJson = """
                                   {
                                     "name": "sample-pkg",
                                     "dependencies": {
                                       "react": "^18.2.0",
                                       "lodash": "^4.17.21"
                                     },
                                     "devDependencies": {
                                       "typescript": "^5.0.0"
                                     }
                                   }
                                   """;

        const string readme = "# Sample Asset\n\nThis is a clean asset package with documentation.";

        await using var zip = CreateZip(
            ("README.md", readme),
            ("package.json", packageJson),
            ("src/index.js", "console.log('hello');"));

        var result = await _sut.Inspect(zip, "asset.zip");

        result.IsSafe.Should().BeTrue();
        result.FileCount.Should().Be(3);
        result.TotalExpandedBytes.Should().BeGreaterThan(0);
        result.ReadmeContent.Should().Be(readme);
        result.ManifestMetadata.Should().NotBeNull();
        result.ManifestMetadata!.Manifests.Should().HaveCount(1);
        result.ManifestMetadata!.Manifests[0].ManifestType.Should().Be("npm");
        result.ManifestMetadata!.Manifests[0].Dependencies.Should().Contain(["react", "lodash", "typescript"]);
    }

    [Fact]
    public async Task Inspect_WhenPathTraversal_ShouldRejectWithSpecificCode()
    {
        await using var zip = CreateZip(("../evil.sh", "#!/bin/sh\nrm -rf /"));

        var result = await _sut.Inspect(zip, "evil.zip");

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_PATH_TRAVERSAL");
    }

    [Fact]
    public async Task Inspect_WhenAbsolutePath_ShouldRejectWithSpecificCode()
    {
        await using var zip = CreateZip(("/etc/shadow", "root:secret"));

        var result = await _sut.Inspect(zip, "evil.zip");

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_ABSOLUTE_PATH");
    }

    [Fact]
    public async Task Inspect_WhenEmptyArchive_ShouldRejectWithSpecificCode()
    {
        await using var ms = new MemoryStream();
        await using (new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) { }
        ms.Position = 0;

        var result = await _sut.Inspect(ms, "empty.zip");

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_EMPTY");
    }

    [Fact]
    public async Task Inspect_WhenNonSeekableValidZip_ShouldReturnSafe()
    {
        await using var zip = CreateZip(("README.md", "# ok"));
        await using var stream = new NonSeekableStream(zip.ToArray());

        var result = await _sut.Inspect(stream, "test.zip");

        result.IsSafe.Should().BeTrue();
    }

    [Fact]
    public async Task Inspect_WhenCompressionRatioExceeded_ShouldReject()
    {
        var zeros = new byte[2 * 1024 * 1024]; // 2 MB of zeros compresses to ~1 KB
        await using var ms = new MemoryStream();
        await using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("bomb.bin", CompressionLevel.Optimal);
            await using var s = await entry.OpenAsync();
            await s.WriteAsync(zeros);
        }

        ms.Position = 0;
        var result = await _sut.Inspect(ms, "bomb.zip");

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_COMPRESSION_RATIO_EXCEEDED");
    }

    [Fact]
    public async Task Inspect_WhenZipContainsNestedZip_ShouldTreatInnerArchiveAsOrdinaryFile()
    {
        await using var inner = CreateZip(("README.md", "# nested"));
        await using var outer = CreateZipWithBinary(("nested.zip", inner.ToArray()));

        var result = await _sut.Inspect(outer, "outer.zip");

        result.IsSafe.Should().BeTrue();
        result.FileCount.Should().Be(1);
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Inspect_WhenTarGzCompressionRatioExceeded_ShouldRejectBeforeFullyExpanding()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveInspectionOptions
        {
            MaxEntries = 10,
            MaxTotalExpandedBytes = 50 * 1024 * 1024,
            MaxEntryExpandedBytes = 50 * 1024 * 1024,
            MaxCompressionRatio = 20.0,
            MaxPathLength = 256,
            MaxPathDepth = 8,
            MaxReadmeBytes = 1024,
            MaxManifestFiles = 1,
            MaxManifestBytes = 1024
        });
        var sut = new ArchiveSafetyInspector(options, NullLogger<ArchiveSafetyInspector>.Instance);
        const int uncompressedBytes = 4 * 1024 * 1024;
        await using var archive = CreateTarGzZeros(uncompressedBytes);

        var result = await sut.Inspect(archive, "bomb.tar.gz");

        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_COMPRESSION_RATIO_EXCEEDED");
        result.TotalExpandedBytes.Should().BeGreaterThan(0);
        result.TotalExpandedBytes.Should().BeLessThan(uncompressedBytes / 4);
    }

    [Fact]
    public async Task Inspect_WhenPlainTarContainsZeros_ShouldNotApplyGzipRatio()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveInspectionOptions
        {
            MaxEntries = 10,
            MaxTotalExpandedBytes = 10 * 1024 * 1024,
            MaxEntryExpandedBytes = 10 * 1024 * 1024,
            MaxCompressionRatio = 2.0,
            MaxPathLength = 256,
            MaxPathDepth = 8,
            MaxReadmeBytes = 1024,
            MaxManifestFiles = 1,
            MaxManifestBytes = 1024
        });
        var sut = new ArchiveSafetyInspector(options, NullLogger<ArchiveSafetyInspector>.Instance);
        await using var tar = CreateTarZeros(2 * 1024 * 1024, gzip: false);

        var result = await sut.Inspect(tar, "zeros.tar");

        result.IsSafe.Should().BeTrue();
        result.FileCount.Should().Be(1);
        result.TotalExpandedBytes.Should().Be(2 * 1024 * 1024);
    }

    [Fact]
    public async Task Inspect_WhenDuplicatePathsDifferOnlyByCase_ShouldReject()
    {
        await using var zip = CreateZip(("README.md", "a"), ("readme.md", "b"));
        var result = await _sut.Inspect(zip, "dup.zip");
        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_DUPLICATE_ENTRY");
    }

    [Fact]
    public async Task Inspect_WhenPathDepthExceeded_ShouldReject()
    {
        var deep = string.Join('/', Enumerable.Repeat("d", 40)) + "/file.txt";
        await using var zip = CreateZip((deep, "x"));
        var result = await _sut.Inspect(zip, "deep.zip");
        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_PATH_TOO_DEEP");
    }

    [Fact]
    public async Task Inspect_WhenReadmeExceedsLimit_ShouldKeepUtf8Boundary()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveInspectionOptions
        {
            MaxEntries = 10,
            MaxTotalExpandedBytes = 1024 * 1024,
            MaxEntryExpandedBytes = 1024 * 1024,
            MaxCompressionRatio = 100,
            MaxPathLength = 256,
            MaxPathDepth = 8,
            MaxReadmeBytes = 16,
            MaxManifestFiles = 1,
            MaxManifestBytes = 1024
        });
        var sut = new ArchiveSafetyInspector(options, NullLogger<ArchiveSafetyInspector>.Instance);
        var readme = "éééééééééééé"; // 2-byte UTF-8 chars
        await using var zip = CreateZip(("README.md", readme));
        var result = await sut.Inspect(zip, "readme.zip");
        result.IsSafe.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.ReadmeContent!).Should().BeLessThanOrEqualTo(16);
    }

    [Fact]
    public async Task Inspect_WhenExpandedBytesExceedEntryLimit_ShouldRejectEvenIfMetadataLooksSmall()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveInspectionOptions
        {
            MaxEntries = 10,
            MaxTotalExpandedBytes = 1024 * 1024,
            MaxEntryExpandedBytes = 8,
            MaxCompressionRatio = 10_000,
            MaxPathLength = 256,
            MaxPathDepth = 8,
            MaxReadmeBytes = 1024,
            MaxManifestFiles = 1,
            MaxManifestBytes = 1024
        });
        var sut = new ArchiveSafetyInspector(options, NullLogger<ArchiveSafetyInspector>.Instance);
        await using var zip = CreateZip(("payload.bin", "0123456789ABCDEF"));
        var result = await sut.Inspect(zip, "over.zip");
        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_ENTRY_TOO_LARGE");
    }

    [Theory]
    [InlineData("asset.tar", false)]
    [InlineData("asset.tar.gz", true)]
    [InlineData("asset.tgz", true)]
    public async Task Inspect_WhenAllowedTarFamily_ShouldReturnSafe(string fileName, bool gzip)
    {
        await using var tar = CreateTar(("README.md", "# tar"), gzip);
        var result = await _sut.Inspect(tar, fileName);
        result.IsSafe.Should().BeTrue();
        result.FileCount.Should().Be(1);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink, "ARCHIVE_SYMLINK_NOT_ALLOWED")]
    [InlineData(TarEntryType.HardLink, "ARCHIVE_SYMLINK_NOT_ALLOWED")]
    [InlineData(TarEntryType.Fifo, "ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED")]
    [InlineData(TarEntryType.CharacterDevice, "ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED")]
    [InlineData(TarEntryType.BlockDevice, "ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED")]
    public async Task Inspect_WhenTarSpecialEntry_ShouldReject(TarEntryType entryType, string errorCode)
    {
        await using var tar = CreateTar(("special", string.Empty, entryType, "target"));
        var result = await _sut.Inspect(tar, "asset.tar");
        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be(errorCode);
    }

    [Fact]
    public void IsAllowedTarEntryType_WhenTypeMetadataMissingOrUnknown_ShouldReject()
    {
        ArchiveSafetyInspector.IsAllowedTarEntryType(null, out var missingCode).Should().BeFalse();
        missingCode.Should().Be("ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED");

        ArchiveSafetyInspector.IsAllowedTarEntryType((TarEntryType)255, out var unknownCode).Should().BeFalse();
        unknownCode.Should().Be("ARCHIVE_SPECIAL_ENTRY_NOT_ALLOWED");
    }

    [Theory]
    [InlineData(TarEntryType.RegularFile, true, "")]
    [InlineData(TarEntryType.Directory, true, "")]
    [InlineData(TarEntryType.ExtendedAttributes, true, "")]
    [InlineData(TarEntryType.GlobalExtendedAttributes, true, "")]
    [InlineData(TarEntryType.LongLink, true, "")]
    [InlineData(TarEntryType.LongPath, true, "")]
    public void IsAllowedTarEntryType_WhenRegularDirectoryOrPaxMetadata_ShouldAllow(
        TarEntryType entryType,
        bool allowed,
        string errorCode)
    {
        ArchiveSafetyInspector.IsAllowedTarEntryType(entryType, out var actual).Should().Be(allowed);
        actual.Should().Be(errorCode);
    }

    [Fact]
    public async Task Inspect_WhenTarContainsDirectoryAndRegularFile_ShouldReturnSafe()
    {
        var tar = new MemoryStream();
        await using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "src"));
            var bytes = "# tar"u8.ToArray();
            using var data = new MemoryStream(bytes);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "README.md")
            {
                DataStream = data
            });
        }

        tar.Position = 0;
        var result = await _sut.Inspect(tar, "asset.tar");
        result.IsSafe.Should().BeTrue();
        result.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task Inspect_WhenRemainingTotalBudgetIsSmall_ShouldStopBeforeFullyExpandingNextEntry()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ArchiveInspectionOptions
        {
            MaxEntries = 10,
            MaxTotalExpandedBytes = 40,
            MaxEntryExpandedBytes = 10 * 1024 * 1024,
            MaxCompressionRatio = 10_000,
            MaxPathLength = 256,
            MaxPathDepth = 8,
            MaxReadmeBytes = 1024,
            MaxManifestFiles = 1,
            MaxManifestBytes = 1024
        });
        var sut = new ArchiveSafetyInspector(options, NullLogger<ArchiveSafetyInspector>.Instance);
        await using var zip = CreateZip(
            ("small.bin", new string('a', 32)),
            ("huge.bin", new string('b', 2 * 1024 * 1024)));
        var result = await sut.Inspect(zip, "budget.zip");
        result.IsSafe.Should().BeFalse();
        result.ErrorCode.Should().Be("ARCHIVE_TOTAL_SIZE_EXCEEDED");
    }

    private static MemoryStream CreateTarGzZeros(int uncompressedBytes) =>
        CreateTarZeros(uncompressedBytes, gzip: true);

    private static MemoryStream CreateTarZeros(int uncompressedBytes, bool gzip)
    {
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            using var data = new MemoryStream(new byte[uncompressedBytes]);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "bomb.bin")
            {
                DataStream = data
            });
        }

        tar.Position = 0;
        if (!gzip)
        {
            return tar;
        }

        var gz = new MemoryStream();
        using (var gzipStream = new GZipStream(gz, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            tar.CopyTo(gzipStream);
        }

        gz.Position = 0;
        return gz;
    }

    private static MemoryStream CreateTar(
        (string Name, string Content) entry,
        bool gzip) =>
        CreateTar((entry.Name, entry.Content, TarEntryType.RegularFile, null), gzip);

    private static MemoryStream CreateTar(
        (string Name, string Content, TarEntryType Type, string? LinkName) entry,
        bool gzip = false)
    {
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(entry.Content);
            using var data = new MemoryStream(bytes);
            TarEntry tarEntry = entry.Type switch
            {
                TarEntryType.RegularFile => new PaxTarEntry(TarEntryType.RegularFile, entry.Name)
                {
                    DataStream = data
                },
                TarEntryType.SymbolicLink => new PaxTarEntry(TarEntryType.SymbolicLink, entry.Name)
                {
                    LinkName = entry.LinkName ?? "target"
                },
                TarEntryType.HardLink => new PaxTarEntry(TarEntryType.HardLink, entry.Name)
                {
                    LinkName = entry.LinkName ?? "target"
                },
                TarEntryType.Fifo => new PaxTarEntry(TarEntryType.Fifo, entry.Name),
                TarEntryType.CharacterDevice => new PaxTarEntry(TarEntryType.CharacterDevice, entry.Name)
                {
                    DeviceMajor = 1,
                    DeviceMinor = 3
                },
                TarEntryType.BlockDevice => new PaxTarEntry(TarEntryType.BlockDevice, entry.Name)
                {
                    DeviceMajor = 8,
                    DeviceMinor = 0
                },
                _ => throw new ArgumentOutOfRangeException(nameof(entry))
            };
            writer.WriteEntry(tarEntry);
        }

        tar.Position = 0;
        if (!gzip)
        {
            return tar;
        }

        var gz = new MemoryStream();
        using (var gzipStream = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            tar.CopyTo(gzipStream);
        }

        gz.Position = 0;
        return gz;
    }

    private static MemoryStream CreateZip(params (string Name, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(content);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private static MemoryStream CreateZipWithBinary(params (string Name, byte[] Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        ms.Position = 0;
        return ms;
    }

    private sealed class NonSeekableStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override bool CanSeek => false;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
}
