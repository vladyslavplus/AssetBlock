using AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using FluentValidation.Results;
using NSubstitute;

namespace AssetBlock.Application.Tests.Validators;

public sealed class ExportSellerAnalyticsSalesCommandValidatorTests
{
    private readonly ExportSellerAnalyticsSalesCommandValidator _validator = new();
    private static readonly DateOnly _validFrom = new(2024, 1, 1);
    private static readonly DateOnly _validTo = new(2024, 1, 11);

    [Fact]
    public async Task Validate_WhenValidCommand_ShouldPass()
    {
        ISellerAnalyticsSalesExportSession session = Substitute.For<ISellerAnalyticsSalesExportSession>();
        using var stream = new MemoryStream();
        var cmd = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            _validFrom,
            _validTo,
            AnalyticsProductTypeFilter.ALL,
            stream,
            session);

        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenSellerIdEmpty_ShouldFail()
    {
        ISellerAnalyticsSalesExportSession session = Substitute.For<ISellerAnalyticsSalesExportSession>();
        using var stream = new MemoryStream();
        var cmd = new ExportSellerAnalyticsSalesCommand(
            Guid.Empty,
            _validFrom,
            _validTo,
            AnalyticsProductTypeFilter.ALL,
            stream,
            session);

        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.SellerId));
    }

    [Fact]
    public async Task Validate_WhenInvalidDateRange_ShouldFail()
    {
        ISellerAnalyticsSalesExportSession session = Substitute.For<ISellerAnalyticsSalesExportSession>();
        using var stream = new MemoryStream();
        var cmd = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            _validTo,
            _validFrom,
            AnalyticsProductTypeFilter.ALL,
            stream,
            session);

        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE));
    }

    [Fact]
    public async Task Validate_WhenOutputStreamNull_ShouldFail()
    {
        ISellerAnalyticsSalesExportSession session = Substitute.For<ISellerAnalyticsSalesExportSession>();
        var cmd = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            _validFrom,
            _validTo,
            AnalyticsProductTypeFilter.ALL,
            null!,
            session);

        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.OutputStream));
    }

    [Fact]
    public async Task Validate_WhenSessionNull_ShouldFail()
    {
        using var stream = new MemoryStream();
        var cmd = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            _validFrom,
            _validTo,
            AnalyticsProductTypeFilter.ALL,
            stream,
            null!);

        ValidationResult result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.Session));
    }
}
