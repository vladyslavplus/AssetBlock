using System.Reflection;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Application.Tests;

public sealed class ErrorCodeMessageCoverageTests
{
    [Fact]
    public void Every_declared_error_code_has_a_nonempty_user_message()
    {
        var codes = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(codes);
        foreach (var code in codes)
        {
            var message = ErrorCodesToErrorMessages.GetMessage(code);

            Assert.False(string.IsNullOrWhiteSpace(message), $"{code} has no message.");
            Assert.True(message != "An error occurred.", $"{code} only resolves to the fallback.");
            Assert.DoesNotContain(code, message, StringComparison.Ordinal);
        }
    }
}
