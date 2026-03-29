using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class DatabaseOptionsValidatorTests
{
    private readonly DatabaseOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenBothAutoMigrateAndEnsureCreated_ReturnsFail()
    {
        var result = _sut.Validate(null, new DatabaseOptions { AutoMigrate = true, EnsureCreated = true });
        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("AutoMigrate");
    }

    [Fact]
    public void Validate_WhenOnlyOneFlag_ReturnsSuccess()
    {
        _sut.Validate(null, new DatabaseOptions { AutoMigrate = true, EnsureCreated = false }).Succeeded.Should().BeTrue();
        _sut.Validate(null, new DatabaseOptions { AutoMigrate = false, EnsureCreated = true }).Succeeded.Should().BeTrue();
        _sut.Validate(null, new DatabaseOptions { AutoMigrate = false, EnsureCreated = false }).Succeeded.Should().BeTrue();
    }
}
