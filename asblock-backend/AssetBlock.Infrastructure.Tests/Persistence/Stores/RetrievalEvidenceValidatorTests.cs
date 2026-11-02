using System.Text.Json;
using System.Text.Json.Nodes;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Infrastructure.Persistence.Stores;
using AwesomeAssertions;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class RetrievalEvidenceValidatorTests
{
    [Theory]
    [InlineData("tokenRank", "0")]
    [InlineData("tokenRank", "21")]
    [InlineData("tokenRank", "true")]
    [InlineData("tokenScore", "1.1")]
    [InlineData("tokenScore", "-0.1")]
    [InlineData("tokenScore", "0")]
    [InlineData("semanticScore", "\"NaN\"")]
    [InlineData("semanticRank", "null")]
    [InlineData("channels", "\"SEMANTIC\"")]
    [InlineData("material", "false")]
    [InlineData("queryFileId", "\"bad\"")]
    public void Validate_WhenCandidateIsMalformed_ShouldReject(string field, string json)
    {
        JsonObject candidate = Candidate();
        candidate["retrieval"]![field] = JsonNode.Parse(json);
        using var document = JsonDocument.Parse(candidate.ToJsonString());
        Action validate = () => RetrievalEvidenceValidator.ParseCandidate(document.RootElement, "SIMILARITY_TOKEN");
        validate.Should().Throw<CodeAnalysisValidationException>();
    }

    [Fact]
    public void Validate_WhenChannelIsAbsent_ShouldRequireExplicitNullAndPreserveZero()
    {
        JsonObject candidate = Candidate();
        candidate["retrieval"]!["channels"] = "SEMANTIC";
        candidate["retrieval"]!["tokenRank"] = null;
        candidate["retrieval"]!["tokenScore"] = null;
        candidate["retrieval"]!["semanticScore"] = 0;
        candidate["retrieval"]!["material"] = false;
        using var document = JsonDocument.Parse(candidate.ToJsonString());
        RetrievalCandidateEvidence? evidence = RetrievalEvidenceValidator.ParseCandidate(document.RootElement, "SIMILARITY_SEMANTIC");
        evidence!.TokenScore.Should().BeNull();
        evidence.SemanticScore.Should().Be(0);
        evidence.Material.Should().BeFalse();
    }

    [Fact]
    public void ValidateSummary_WhenChannelsAndReportOverflowAreNonzero_ShouldAccept()
    {
        JsonObject root = Summary();
        JsonObject query = root["retrievalSummary"]!["queries"]![0]!.AsObject();
        query["tokenCandidates"] = 25;
        query["tokenRetained"] = 20;
        query["tokenOverflow"] = 5;
        query["semanticCandidates"] = 30;
        query["semanticRetained"] = 20;
        query["semanticOverflow"] = 10;
        query["unionRetained"] = 30;
        query["reportOmitted"] = 29;
        query["reportOmittedToken"] = 19;
        query["reportOmittedSemantic"] = 19;
        Validate(root, 29, Candidate()).Queries.Single().TokenOverflow.Should().Be(5);
        Action insufficient = () => Validate(root, 28, Candidate());
        insufficient.Should().Throw<CodeAnalysisValidationException>();
    }

    [Theory]
    [InlineData("duplicate-reference")]
    [InlineData("wrong-token-tie-order")]
    [InlineData("unmarked-top-tie")]
    public void ValidateSummary_WhenIdentityTieOrderOrMaterialIsInvalid_ShouldReject(string condition)
    {
        JsonObject first = Candidate();
        JsonObject second = Candidate();
        second["retrieval"]!["tokenRank"] = 2;
        second["retrieval"]!["semanticRank"] = 2;
        second["foreign"]!["fileId"] = new string('c', 64);
        JsonObject root = Summary(2);
        Validate(root, 0, first, second).Queries.Should().ContainSingle();
        switch (condition)
        {
            case "duplicate-reference":
                second["foreign"]!["fileId"] = new string('b', 64);
                break;
            case "wrong-token-tie-order":
                first["foreign"]!["fileId"] = new string('c', 64);
                second["foreign"]!["fileId"] = new string('b', 64);
                break;
            case "unmarked-top-tie":
                second["retrieval"]!["material"] = false;
                break;
        }
        Action validate = () => Validate(root, 0, first, second);
        validate.Should().Throw<CodeAnalysisValidationException>();
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(3, 1, 1)]
    public void ValidateSummary_WhenOmittedChannelUnionIsImpossible_ShouldReject(int omitted, int token, int semantic)
    {
        JsonObject root = Summary(3);
        JsonObject query = root["retrievalSummary"]!["queries"]![0]!.AsObject();
        query["reportOmitted"] = omitted;
        query["reportOmittedToken"] = token;
        query["reportOmittedSemantic"] = semantic;
        Action validate = () => Validate(root, omitted, Candidate());
        validate.Should().Throw<CodeAnalysisValidationException>();
    }

    [Fact]
    public void ValidateSummary_WhenNoPositiveTokenExists_ShouldHaveNoMaterialRows()
    {
        JsonObject candidate = Candidate();
        candidate["retrieval"]!["channels"] = "SEMANTIC";
        candidate["retrieval"]!["tokenRank"] = null;
        candidate["retrieval"]!["tokenScore"] = null;
        candidate["retrieval"]!["material"] = false;
        JsonObject root = Summary();
        JsonObject query = root["retrievalSummary"]!["queries"]![0]!.AsObject();
        query["tokenCandidates"] = 0;
        query["tokenRetained"] = 0;
        query["tokenMaxScore"] = null;
        Validate(root, 0, candidate).Queries.Single().TokenMaxScore.Should().BeNull();
        candidate["retrieval"]!["material"] = true;
        Action invalid = () => Validate(root, 0, candidate);
        invalid.Should().Throw<CodeAnalysisValidationException>();
    }

    [Fact]
    public void ValidateSummary_WhenRankOneWasOmitted_ShouldUseItsExplicitMeasuredMaximum()
    {
        JsonObject candidate = Candidate();
        candidate["retrieval"]!["tokenRank"] = 2;
        JsonObject root = Summary();
        JsonObject query = root["retrievalSummary"]!["queries"]![0]!.AsObject();
        query["tokenCandidates"] = 2;
        query["tokenRetained"] = 2;
        query["unionRetained"] = 2;
        query["reportOmitted"] = 1;
        query["reportOmittedToken"] = 1;
        Validate(root, 1, candidate).Queries.Should().ContainSingle();
        candidate["retrieval"]!["tokenScore"] = 0.7;
        candidate["retrieval"]!["material"] = false;
        Validate(root, 1, candidate).Queries.Should().ContainSingle();
    }

    [Fact]
    public void Serialize_WhenSummaryIsPersisted_ShouldUseStrictCamelCase()
    {
        var persisted = new PersistedRetrievalSummary("snapshot", "revision", 0,
            new RetrievalReportSummary(20, 20, 40, []));
        var json = JsonSerializer.Serialize(persisted, RetrievalEvidenceValidator.JsonOptions);
        json.Should().Contain("\"referenceSnapshotId\"").And.Contain("\"tokenTop\"").And.NotContain("TokenTop");
        Action wrongCase = () => JsonSerializer.Deserialize<PersistedRetrievalSummary>(json.Replace("tokenTop", "TokenTop"), RetrievalEvidenceValidator.JsonOptions);
        wrongCase.Should().Throw<JsonException>();
    }

    private static RetrievalReportSummary Validate(JsonObject root, int overflow, params JsonObject[] candidates)
    {
        var findings = new List<ValidatedReportFinding>();
        foreach (JsonObject candidate in candidates)
        {
            using var document = JsonDocument.Parse(candidate.ToJsonString());
            var type = candidate["retrieval"]!["tokenRank"] is null ? "SIMILARITY_SEMANTIC" : "SIMILARITY_TOKEN";
            RetrievalCandidateEvidence evidence = RetrievalEvidenceValidator.ParseCandidate(document.RootElement, type)!;
            findings.Add(new ValidatedReportFinding(Guid.NewGuid().ToString("N"), type, "own.py", "own", null, null,
                candidate["foreign"]!["fileId"]!.GetValue<string>(), "FOREIGN", "PRIVATE", null,
                evidence.TokenScore, evidence.SemanticScore, evidence.TokenRank, evidence.SemanticRank, "reason", "action",
                RetrievalEvidenceJson: JsonSerializer.Serialize(evidence, RetrievalEvidenceValidator.JsonOptions)));
        }
        using var body = JsonDocument.Parse(root.ToJsonString());
        return RetrievalEvidenceValidator.ValidateSummary(body.RootElement, findings, overflow);
    }

    private static JsonObject Summary(int retained = 1) => new()
    {
        ["coverage"] = new JsonObject { ["analyzedFiles"] = 1 },
        ["retrievalSummary"] = new JsonObject
        {
            ["tokenTop"] = 20,
            ["semanticTop"] = 20,
            ["unionBudget"] = 40,
            ["queries"] = new JsonArray(new JsonObject
            {
                ["queryFileId"] = new string('a', 64),
                ["tokenMaxScore"] = 0.8,
                ["tokenCandidates"] = retained,
                ["tokenRetained"] = retained,
                ["tokenOverflow"] = 0,
                ["semanticCandidates"] = retained,
                ["semanticRetained"] = retained,
                ["semanticOverflow"] = 0,
                ["unionRetained"] = retained,
                ["reportOmitted"] = 0,
                ["reportOmittedToken"] = 0,
                ["reportOmittedSemantic"] = 0
            })
        }
    };

    private static JsonObject Candidate() => new()
    {
        ["fileId"] = new string('a', 64),
        ["foreign"] = new JsonObject { ["fileId"] = new string('b', 64) },
        ["retrieval"] = new JsonObject
        {
            ["queryFileId"] = new string('a', 64),
            ["queryUnit"] = "FILE",
            ["channels"] = "TOKEN_AND_SEMANTIC",
            ["tokenRank"] = 1,
            ["tokenScore"] = 0.8,
            ["semanticRank"] = 1,
            ["semanticScore"] = 0.4,
            ["material"] = true
        }
    };
}
