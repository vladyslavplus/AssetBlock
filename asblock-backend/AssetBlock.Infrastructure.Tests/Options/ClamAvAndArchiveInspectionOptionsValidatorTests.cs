using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Configuration;

namespace AssetBlock.Infrastructure.Tests.Options;

public sealed class ClamAvOptionsValidatorTests
{
    [Fact]
    public void Validate_WhenProcessingEnabledAndClamAvDisabled_ShouldFail()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true"
        }).Build();
        var sut = new ClamAvOptionsValidator(config);

        var result = sut.Validate(null, new ClamAvOptions { Enabled = false, Host = "127.0.0.1", Port = 3310 });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenProcessingEnabledAndClamAvConfigured_ShouldSucceed()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true"
        }).Build();
        var sut = new ClamAvOptionsValidator(config);

        var result = sut.Validate(null, new ClamAvOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 3310,
            ConnectTimeoutMs = 1000,
            ReadTimeoutMs = 1000,
            WriteTimeoutMs = 1000,
            MaxStreamBytes = 262144000,
            MaxResponseBytes = 4096
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenMaxSignatureAgeIsOutOfBounds_ShouldFail()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true"
        }).Build();
        var sut = new ClamAvOptionsValidator(config);

        var tooShort = sut.Validate(null, new ClamAvOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 3310,
            MaxStreamBytes = 262144000,
            MaxSignatureAge = TimeSpan.FromMinutes(30)
        });
        tooShort.Failed.Should().BeTrue();
        tooShort.Failures.Should().Contain(f => f.Contains("MaxSignatureAge"));

        var tooLong = sut.Validate(null, new ClamAvOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 3310,
            MaxStreamBytes = 262144000,
            MaxSignatureAge = TimeSpan.FromDays(8)
        });
        tooLong.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenDaemonMaxStreamBytesIsNegative_ShouldFail()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true"
        }).Build();
        var sut = new ClamAvOptionsValidator(config);

        var result = sut.Validate(null, new ClamAvOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 3310,
            MaxStreamBytes = 262144000,
            DaemonMaxStreamBytes = -1
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("DaemonMaxStreamBytes"));
    }

    [Fact]
    public void Validate_WhenMaxStreamBytesBelowUploadLimit_ShouldFail()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true",
            ["FileUpload:MaxFileBytes"] = "262144000"
        }).Build();
        var sut = new ClamAvOptionsValidator(config);

        var result = sut.Validate(null, new ClamAvOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 3310,
            MaxStreamBytes = 1024
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MaxStreamBytes"));
    }

    [Fact]
    public void Validate_WhenConnectTimeoutExceedsUpperBound_ShouldFail()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true"
        }).Build();
        var sut = new ClamAvOptionsValidator(config);

        var result = sut.Validate(null, new ClamAvOptions
        {
            Enabled = true,
            Host = "127.0.0.1",
            Port = 3310,
            ConnectTimeoutMs = ClamAvOptions.MAX_CONNECT_TIMEOUT_MS + 1,
            MaxStreamBytes = 262144000
        });

        result.Failed.Should().BeTrue();
    }
}

public sealed class ArchiveInspectionOptionsValidatorTests
{
    [Fact]
    public void Validate_WhenCompressionRatioIsNonFinite_ShouldFail()
    {
        var sut = new ArchiveInspectionOptionsValidator();
        var result = sut.Validate(null, new ArchiveInspectionOptions { MaxCompressionRatio = double.PositiveInfinity });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenEntryLimitExceedsTotal_ShouldFail()
    {
        var sut = new ArchiveInspectionOptionsValidator();
        var result = sut.Validate(null, new ArchiveInspectionOptions
        {
            MaxEntryExpandedBytes = 200,
            MaxTotalExpandedBytes = 100
        });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPathDepthExceedsUpperBound_ShouldFail()
    {
        var sut = new ArchiveInspectionOptionsValidator();
        var result = sut.Validate(null, new ArchiveInspectionOptions
        {
            MaxPathDepth = ArchiveInspectionOptions.MAX_PATH_DEPTH_UPPER + 1
        });
        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenManifestFilesExceedUpperBound_ShouldFail()
    {
        var sut = new ArchiveInspectionOptionsValidator();
        var result = sut.Validate(null, new ArchiveInspectionOptions
        {
            MaxManifestFiles = ArchiveInspectionOptions.MAX_MANIFEST_FILES_UPPER + 1
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MaxManifestFiles"));
    }

    [Fact]
    public void Validate_WhenManifestFilesExceedMaxEntries_ShouldFail()
    {
        var sut = new ArchiveInspectionOptionsValidator();
        var result = sut.Validate(null, new ArchiveInspectionOptions
        {
            MaxEntries = 4,
            MaxManifestFiles = 8
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("MaxManifestFiles cannot be greater than MaxEntries"));
    }
}
