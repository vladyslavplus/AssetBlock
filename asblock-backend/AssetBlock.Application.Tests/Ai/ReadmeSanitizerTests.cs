using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Ai;

public sealed class ReadmeSanitizerTests
{
    [Fact]
    public void Sanitize_WhenNullOrWhiteSpace_ShouldReturnNull()
    {
        ReadmeSanitizer.Sanitize(null).Should().BeNull();
        ReadmeSanitizer.Sanitize(string.Empty).Should().BeNull();
        ReadmeSanitizer.Sanitize("   \n\t  ").Should().BeNull();
    }

    [Fact]
    public void Sanitize_WhenFencedCodeBlocksPresent_ShouldRemoveThem()
    {
        const string input = """
                             # Project Title
                             Here is an example:
                             ```csharp
                             var secret = "super-secret-key";
                             Console.WriteLine(secret);
                             ```
                             And another section.
                             ~~~bash
                             npm install evil-package
                             ~~~
                             End of readme.
                             """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("super-secret-key");
        result.Should().NotContain("evil-package");
        result.Should().NotContain("```");
        result.Should().NotContain("~~~");
        result.Should().Contain("# Project Title");
        result.Should().Contain("And another section.");
        result.Should().Contain("End of readme.");
    }

    [Fact]
    public void Sanitize_WhenUnclosedFence_ShouldFailClosedAndDropRemainder()
    {
        const string input = """
                             # Safe Header
                             Some valid intro text.
                             ```python
                             secret_in_unclosed_block = 12345
                             injection_attack = True
                             never_closed_rest_of_document
                             """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().Contain("# Safe Header");
        result.Should().Contain("Some valid intro text.");
        result.Should().NotContain("secret_in_unclosed_block");
        result.Should().NotContain("injection_attack");
        result.Should().NotContain("never_closed_rest_of_document");
    }

    [Fact]
    public void Sanitize_WhenUnclosedTildeFence_ShouldFailClosed()
    {
        const string input = """
                             Valid title
                             ~~~
                             dangerous_content = 42
                             """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().Be("Valid title");
    }

    [Fact]
    public void Sanitize_WhenCrlfLineEndingsWithCredentials_ShouldDropEntireCredentialLines()
    {
        var input = "# Title\r\nAPI_KEY=123456789\r\nPassword: secretPassword\r\nValid description line.\r\n";

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("API_KEY");
        result.Should().NotContain("123456789");
        result.Should().NotContain("Password");
        result.Should().NotContain("secretPassword");
        result.Should().Contain("# Title");
        result.Should().Contain("Valid description line.");
    }

    [Fact]
    public void Sanitize_WhenCrlfLineEndingsWithIndentedCode_ShouldDropIndentedCode()
    {
        var input = "Intro line\r\n    var x = 100;\r\n\tlet y = 200;\r\nOutro line\r\n";

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("var x = 100");
        result.Should().NotContain("let y = 200");
        result.Should().Contain("Intro line");
        result.Should().Contain("Outro line");
    }

    [Fact]
    public void Sanitize_WhenUrlsPresent_ShouldRemoveThem()
    {
        const string input = """
                              Visit our site at https://example.com/download or http://insecure.org/payload.
                              Also check ftp://files.org/data and ssh://git@github.com/repo.
                              Or www.example.org.
                              Plain text remains.
                              """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("https://example.com/download");
        result.Should().NotContain("http://insecure.org/payload");
        result.Should().NotContain("ftp://files.org/data");
        result.Should().NotContain("ssh://git@github.com/repo");
        result.Should().NotContain("www.example.org");
        result.Should().Contain("Plain text remains.");
    }

    [Fact]
    public void Sanitize_WhenEmailPresent_ShouldRemoveThem()
    {
        const string input = "Contact the author at support@example.com or admin.team+extra@corp.co.uk for help.";

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("support@example.com");
        result.Should().NotContain("admin.team+extra@corp.co.uk");
        result.Should().Contain("Contact the author at");
    }

    [Fact]
    public void Sanitize_WhenCredentialKeywordsPresent_ShouldDropEntireLines()
    {
        const string input = """
                             # Config info
                             API_KEY=sk-test-123456789
                             My password is: hunter2
                             Here is the private key: ABCDEF
                             token = abcdef123456
                             license-key: XXXX-YYYY
                             bearer_token: secret-bearer
                             Normal description line.
                             """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("API_KEY");
        result.Should().NotContain("sk-test-123456789");
        result.Should().NotContain("hunter2");
        result.Should().NotContain("private key");
        result.Should().NotContain("abcdef123456");
        result.Should().NotContain("license-key");
        result.Should().NotContain("bearer_token");
        result.Should().Contain("# Config info");
        result.Should().Contain("Normal description line.");
    }

    [Fact]
    public void Sanitize_WhenOnlySecretsOrCode_ShouldReturnNull()
    {
        const string input = """
                             ```
                             all code
                             ```
                             password=123
                             API_KEY=456
                             """;

        var result = ReadmeSanitizer.Sanitize(input);
        result.Should().BeNull();
    }

    [Fact]
    public void Sanitize_WhenPrefixedCredentialsPresent_ShouldDropEntireLines()
    {
        const string input = """
                             # Setup Guide
                             GITHUB_TOKEN=ghp_abcdef123456
                             AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
                             MY_PASSWORD=mySuperSecretPassword123
                             Valid descriptive sentence for asset listing.
                             """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().NotContain("GITHUB_TOKEN");
        result.Should().NotContain("ghp_abcdef123456");
        result.Should().NotContain("AWS_SECRET_ACCESS_KEY");
        result.Should().NotContain("wJalrXUtnFEMI");
        result.Should().NotContain("MY_PASSWORD");
        result.Should().NotContain("mySuperSecretPassword123");
        result.Should().Contain("# Setup Guide");
        result.Should().Contain("Valid descriptive sentence for asset listing.");
    }

    [Fact]
    public void Sanitize_WhenFakeClosingFenceWithSuffix_ShouldRemainInFenceAndDropSubsequentText()
    {
        const string input = """
                             # Safe Header
                             ```csharp
                             var x = 1;
                             ```not-a-real-close
                             SECRET_LINE_AFTER_FAKE_CLOSE=12345
                             injection_prompt_payload
                             """;

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().Contain("# Safe Header");
        result.Should().NotContain("SECRET_LINE_AFTER_FAKE_CLOSE");
        result.Should().NotContain("injection_prompt_payload");
        result.Should().NotContain("var x = 1");
    }

    [Fact]
    public void Sanitize_WhenRealClosingFenceWithTrailingWhitespace_ShouldCloseFenceProperly()
    {
        const string input = "# Safe Header\n```csharp\nvar code = 123;\n```   \t\nValid text after closed fence.\n";

        var result = ReadmeSanitizer.Sanitize(input);

        result.Should().NotBeNull();
        result.Should().Contain("# Safe Header");
        result.Should().Contain("Valid text after closed fence.");
        result.Should().NotContain("var code = 123");
    }

    [Fact]
    public void Sanitize_WhenTextExceedsMaxLength_ShouldTruncateCleanly()
    {
        var longLine = new string('a', 500) + "\n";
        var builder = new System.Text.StringBuilder();
        while (builder.Length < ListingSuggestionBounds.README_TEXT_MAX_CHARS + 5000)
        {
            builder.Append(longLine);
        }

        var result = ReadmeSanitizer.Sanitize(builder.ToString());

        result.Should().NotBeNull();
        result.Length.Should().BeLessThanOrEqualTo(ListingSuggestionBounds.README_TEXT_MAX_CHARS);
    }
}
