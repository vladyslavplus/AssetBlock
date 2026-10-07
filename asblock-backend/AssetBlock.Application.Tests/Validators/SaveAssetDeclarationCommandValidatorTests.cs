using AssetBlock.Application.UseCases.Assets.SaveAssetDeclaration;
using AssetBlock.Application.UseCases.Assets.SaveAssetDraft;
using AssetBlock.Domain.Core.Dto.Assets;
using AwesomeAssertions;
using FluentValidation.TestHelper;

namespace AssetBlock.Application.Tests.Validators;

public class SaveAssetDeclarationCommandValidatorTests
{
    private readonly SaveAssetDeclarationCommandValidator _validator = new();

    private static SaveAssetDeclarationCommand ValidCommand(SourceDeclarationPayload? declaration = null)
    {
        SourceDeclarationPayload payload = declaration ?? new SourceDeclarationPayload(
            OwnContributionSummary: "Authored the pipeline.",
            OwnChanges: null,
            EarlierWork: null,
            RedistributionAcknowledged: true,
            DisclosurePolicyVersion: "disclosure-v1",
            Components:
            [
                new SourceDeclarationComponent(
                    ComponentId: "c1",
                    Name: "left-pad",
                    PackagePathOrRange: null,
                    SourceUrl: "https://example.com/left-pad",
                    KnownVersion: "1.0.0",
                    License: "MIT",
                    NoticeLocations: [],
                    Modifications: null,
                    PermissionEvidenceReferences: [],
                    Origin: SourceComponentOrigin.THIRD_PARTY)
            ]);

        return new SaveAssetDeclarationCommand(
            Guid.NewGuid(),
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            payload);
    }

    [Fact]
    public void Validate_WhenDeclarationIsNull_ShouldFailWithoutThrowing()
    {
        SaveAssetDeclarationCommand command = ValidCommand() with { Declaration = null! };

        _validator.TestValidate(command).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenComponentsCollectionIsNull_ShouldFailWithoutThrowing()
    {
        SourceDeclarationPayload declaration = ValidCommand().Declaration;
        SourceDeclarationPayload nullComponents = declaration with { Components = null! };

        _validator.TestValidate(ValidCommand(nullComponents)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenComponentElementIsNull_ShouldFailWithoutThrowing()
    {
        SourceDeclarationPayload declaration = ValidCommand().Declaration;
        SourceDeclarationPayload withNullElement = declaration with { Components = [null!, declaration.Components[0]] };

        _validator.TestValidate(ValidCommand(withNullElement)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenComponentIdIsNull_ShouldFailWithoutThrowing()
    {
        SourceDeclarationPayload declaration = ValidCommand().Declaration;
        SourceDeclarationPayload withNullId = declaration with
        {
            Components =
            [
                declaration.Components[0] with { ComponentId = null! }
            ]
        };

        _validator.TestValidate(ValidCommand(withNullId)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenNoticeLocationsContainNull_ShouldFailWithoutThrowing()
    {
        SourceDeclarationPayload declaration = ValidCommand().Declaration;
        SourceDeclarationPayload withNullRef = declaration with
        {
            Components =
            [
                declaration.Components[0] with { NoticeLocations = [null!] }
            ]
        };

        _validator.TestValidate(ValidCommand(withNullRef)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenPermissionEvidenceReferencesIsNull_ShouldFailWithoutThrowing()
    {
        SourceDeclarationPayload declaration = ValidCommand().Declaration;
        SourceDeclarationPayload withNullRefs = declaration with
        {
            Components =
            [
                declaration.Components[0] with { PermissionEvidenceReferences = null! }
            ]
        };

        _validator.TestValidate(ValidCommand(withNullRefs)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenValidDeclaration_ShouldPass()
    {
        _validator.TestValidate(ValidCommand()).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_WhenDuplicateComponentIds_ShouldFail()
    {
        SourceDeclarationPayload declaration = ValidCommand().Declaration;
        SourceDeclarationPayload duplicated = declaration with { Components = [declaration.Components[0], declaration.Components[0]] };

        _validator.TestValidate(ValidCommand(duplicated)).Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Validate_WhenMaterialSaveCommandHasNullMaterialTitle_ShouldFailWithoutThrowing()
    {
        // Mirrors the SaveAssetDraft guard: the handler trims Title, so a null must fail validation.
        var validator = new SaveAssetDraftCommandValidator();
        var command = new SaveAssetDraftCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            new SellerDraftMaterialPayload(null!, null, Guid.NewGuid(), []));

        validator.TestValidate(command).Errors.Should().NotBeEmpty();
    }
}
