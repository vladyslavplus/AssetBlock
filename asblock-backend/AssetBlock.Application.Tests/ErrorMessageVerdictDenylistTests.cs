using System.Reflection;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Application.Tests;

/// <summary>
/// Backend error messages (every declared ErrorCodes message) are user-facing copy and must not state a verdict about
/// originality or ownership. This test checks those messages for the terms listed below. It is not the frontend
/// denylist: it does not check notification text, and its patterns are narrower than the frontend audit's.
/// </summary>
public sealed class ErrorMessageVerdictDenylistTests
{
    private static readonly string[] _prohibitedTerms =
    [
        "stolen",
        "theft",
        "thief",
        "plagiar",
        "infring",
        "pirat",
        "copied from",
        "illegal",
    ];

    private static readonly Regex _percentageAttachedToVerdict = new(
        @"%\s*(stolen|theft|thief|plagiar|infring|pirat|copied|illegal)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Backend_error_messages_state_no_verdict_wording()
    {
        var offenders = DeclaredErrorCodes()
            .Select(code => (Code: code, Message: ErrorCodesToErrorMessages.GetMessage(code)))
            .Select(item => (item.Code, Violation: VerdictViolation(item.Message)))
            .Where(item => item.Violation is not null)
            .Select(item => $"{item.Code}: {item.Violation}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("This content was copied from another asset.")]
    [InlineData("Likely 80% stolen material.")]
    [InlineData("Matches an infringing package.")]
    [InlineData("A possible THEFT of code.")]
    [InlineData("Shows 47% copied code.")]
    public void Verdict_wording_is_detected_case_insensitively(string message)
    {
        Assert.NotNull(VerdictViolation(message));
    }

    [Theory]
    [InlineData("Shows similar structure to a reference asset.")]
    [InlineData("Seller responses are outstanding.")]
    [InlineData("Matches 47% of the reference tokens.")]
    public void Neutral_wording_is_not_flagged(string message)
    {
        Assert.Null(VerdictViolation(message));
    }

    private static string? VerdictViolation(string message)
    {
        var term = _prohibitedTerms.FirstOrDefault(item => message.Contains(item, StringComparison.OrdinalIgnoreCase));
        if (term is not null)
        {
            return $"prohibited term '{term}' in: {message}";
        }

        Match percentage = _percentageAttachedToVerdict.Match(message);
        return percentage.Success ? $"percentage attached to a verdict word in: {message}" : null;
    }

    private static string[] DeclaredErrorCodes()
    {
        return typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
    }
}
