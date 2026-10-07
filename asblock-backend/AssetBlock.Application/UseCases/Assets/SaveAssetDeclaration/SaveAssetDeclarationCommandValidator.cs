using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.SaveAssetDeclaration;

internal sealed class SaveAssetDeclarationCommandValidator : AbstractValidator<SaveAssetDeclarationCommand>
{
    public SaveAssetDeclarationCommandValidator()
    {
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.OperationId).NotEmpty();
        RuleFor(c => c.ExpectedWorkspaceRevision).GreaterThan(0);

        // The declaration is not guaranteed non-null before these rules run; every nested
        // rule lives inside DependentRules or is null-safe so a malformed direct backend
        // request produces a validation result, never a NullReferenceException.
        RuleFor(c => c.Declaration)
            .NotNull()
            .WithMessage("Declaration payload is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.Declaration.OwnContributionSummary)
                    .NotNull()
                    .MaximumLength(SellerDraftLimits.EXPLANATION_MAX_LENGTH);

                RuleFor(c => c.Declaration.OwnChanges)
                    .MaximumLength(SellerDraftLimits.EXPLANATION_MAX_LENGTH)
                    .When(c => c.Declaration.OwnChanges is not null);

                RuleFor(c => c.Declaration.EarlierWork)
                    .MaximumLength(SellerDraftLimits.EXPLANATION_MAX_LENGTH)
                    .When(c => c.Declaration.EarlierWork is not null);

                RuleFor(c => c.Declaration.DisclosurePolicyVersion)
                    .NotEmpty()
                    .MaximumLength(64);

                RuleFor(c => c.Declaration.Components)
                    .NotNull()
                    .WithMessage("Components collection is required.")
                    .DependentRules(() =>
                    {
                        RuleFor(c => c.Declaration.Components)
                            .Must(components => components!.Count <= SellerDraftLimits.COMPONENTS_MAX_COUNT)
                            .WithMessage($"Declaration may contain at most {SellerDraftLimits.COMPONENTS_MAX_COUNT} components.");

                        // Uniqueness only runs against valid, non-null component ids; it must not
                        // dereference null elements before they are rejected by their own rules.
                        RuleFor(c => c.Declaration.Components)
                            .Must(components => components!
                                .Where(x => x?.ComponentId != null)
                                .Select(x => x.ComponentId.Trim())
                                .Count(id => id.Length > 0)
                                == components!
                                    .Where(x => x?.ComponentId != null)
                                    .Select(x => x.ComponentId.Trim())
                                    .Distinct(StringComparer.Ordinal)
                                    .Count())
                            .WithMessage("Component ids must be unique within the declaration.");

                        RuleForEach(c => c.Declaration!.Components)
                            .NotNull()
                            .WithMessage("Component entries are required.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x is null || (x.ComponentId is not null
                                && x.ComponentId.Trim().Length > 0
                                && x.ComponentId.Trim().Length <= SellerDraftLimits.COMPONENT_ID_MAX_LENGTH))
                            .WithMessage("Component id is required and must not exceed "
                                + $"{SellerDraftLimits.COMPONENT_ID_MAX_LENGTH} characters.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x is null || (x.Name is not null
                                && x.Name.Trim().Length > 0
                                && x.Name.Length <= SellerDraftLimits.COMPONENT_NAME_MAX_LENGTH))
                            .WithMessage("Component name is required and must not exceed "
                                + $"{SellerDraftLimits.COMPONENT_NAME_MAX_LENGTH} characters.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x?.PackagePathOrRange is null
                                       || x.PackagePathOrRange.Length <= SellerDraftLimits.COMPONENT_PATH_MAX_LENGTH)
                            .WithMessage("Component package path must not exceed "
                                + $"{SellerDraftLimits.COMPONENT_PATH_MAX_LENGTH} characters.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x is null || (x.SourceUrl is not null
                                && x.SourceUrl.Length <= SellerDraftLimits.COMPONENT_SOURCE_URL_MAX_LENGTH
                                && Uri.TryCreate(x.SourceUrl, UriKind.Absolute, out Uri? uri)
                                && uri.Scheme == Uri.UriSchemeHttps))
                            .WithMessage("Source URLs must be absolute HTTPS URIs.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x?.KnownVersion is null
                                       || x.KnownVersion.Length <= SellerDraftLimits.COMPONENT_KNOWN_VERSION_MAX_LENGTH)
                            .WithMessage("Component known version must not exceed "
                                + $"{SellerDraftLimits.COMPONENT_KNOWN_VERSION_MAX_LENGTH} characters.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x is null || (x.License is not null
                                && x.License.Trim().Length > 0
                                && x.License.Length <= SellerDraftLimits.COMPONENT_LICENSE_MAX_LENGTH))
                            .WithMessage("Component license is required and must not exceed "
                                + $"{SellerDraftLimits.COMPONENT_LICENSE_MAX_LENGTH} characters.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x is null || (x.NoticeLocations is not null
                                && x.NoticeLocations.Count <= SellerDraftLimits.NOTICE_LOCATIONS_MAX_COUNT
                                && x.NoticeLocations.All(item => item is not null && item.Length <= SellerDraftLimits.COMPONENT_NOTICE_ITEM_MAX_LENGTH)))
                            .WithMessage("Component notice locations are required, at most "
                                + $"{SellerDraftLimits.NOTICE_LOCATIONS_MAX_COUNT} entries of at most "
                                + $"{SellerDraftLimits.COMPONENT_NOTICE_ITEM_MAX_LENGTH} characters.");

                        RuleForEach(c => c.Declaration!.Components)
                            .Must(x => x is null || (x.PermissionEvidenceReferences is not null
                                && x.PermissionEvidenceReferences.Count <= SellerDraftLimits.EVIDENCE_REFERENCES_MAX_COUNT
                                && x.PermissionEvidenceReferences.All(item => item is not null && item.Length <= SellerDraftLimits.COMPONENT_REF_ITEM_MAX_LENGTH)))
                            .WithMessage("Component permission evidence references are required, at most "
                                + $"{SellerDraftLimits.EVIDENCE_REFERENCES_MAX_COUNT} entries of at most "
                                + $"{SellerDraftLimits.COMPONENT_REF_ITEM_MAX_LENGTH} characters.");
                    });
            });
    }
}
