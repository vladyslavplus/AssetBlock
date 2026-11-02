using AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
namespace AssetBlock.Application.Tests.Validators;

public sealed class SaveSellerEvidenceCommandValidatorTests
{
    [Theory]
    [InlineData(SellerResponseType.NO_EVIDENCE, "", true)]
    [InlineData(SellerResponseType.PRIOR_WORK, " ", false)]
    [InlineData(SellerResponseType.PERMITTED_COMPONENT, "Permission described.", true)]
    [InlineData((SellerResponseType)999, "Unknown", false)]
    public void Validate_WhenTypeOrExplanationVaries_ShouldEnforceBoundedResponse(SellerResponseType type, string text, bool valid)
    {
        var command = new SaveSellerEvidenceCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), type, text, 1, 0, Guid.NewGuid());
        new SaveSellerEvidenceCommandValidator().Validate(command).IsValid.Should().Be(valid);
    }
    [Theory]
    [InlineData("text")]
    [InlineData("binding")]
    [InlineData("revision")]
    [InlineData("null")]
    [InlineData("nul")]
    public void Validate_WhenInputIsInvalid_ShouldReject(string invalid)
    {
        var command = new SaveSellerEvidenceCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), SellerResponseType.NO_EVIDENCE, "", 1, 0, Guid.NewGuid());
        command = invalid switch
        {
            "text" => command with { Text = new string('x', 2001) },
            "binding" => command with { FindingId = null },
            "revision" => command with { ExpectedEvidenceRevision = -1 },
            "null" => command with { Text = null! },
            _ => command with { Text = "invalid\0text" }
        };
        new SaveSellerEvidenceCommandValidator().Validate(command).IsValid.Should().BeFalse();
    }
}
