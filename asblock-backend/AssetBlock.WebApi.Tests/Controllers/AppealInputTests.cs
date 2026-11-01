using AssetBlock.WebApi.Controllers;
using AwesomeAssertions;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class AppealInputTests
{
    [Fact]
    public void Validate_WhenRequiredFieldsAreMissing_ShouldReject()
    {
        var input = new AssetsController.AppealInput();
        FluentValidation.Results.ValidationResult result = new AssetsController.AppealInputValidator().Validate(input);
        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.PropertyName).Should().Contain(["WorkspaceId", "ExpectedWorkspaceRevision", "ExpectedCaseRevision", "OperationId", "Rationale"]);
    }
}
