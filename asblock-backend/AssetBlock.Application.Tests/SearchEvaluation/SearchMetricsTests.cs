using AssetBlock.SearchEvaluation.Metrics;
using AssetBlock.SearchEvaluation.Validation;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.SearchEvaluation;

public class SearchMetricsTests
{
    [Fact]
    public void CalculateDcgAtK_WhenSingleItemAtRank1WithGrade3_ShouldEqual7()
    {
        var retrieved = new List<string> { "doc1" };
        var groundTruth = new Dictionary<string, int> { ["doc1"] = 3 };

        // Gain: (2^3 - 1) / log2(1 + 1) = 7.0 / 1.0 = 7.0
        var dcg = SearchMetrics.CalculateDcgAtK(retrieved, groundTruth);
        dcg.Should().BeApproximately(7.0, 0.0001);
    }

    [Fact]
    public void CalculateNdcgAt10_WhenPerfectRanking_ShouldEqual1()
    {
        var retrieved = new List<string> { "doc1", "doc2", "doc3" };
        var groundTruth = new Dictionary<string, int>
        {
            ["doc1"] = 3,
            ["doc2"] = 2,
            ["doc3"] = 1
        };

        var ndcg = SearchMetrics.CalculateNdcgAt10(retrieved, groundTruth);
        ndcg.Should().BeApproximately(1.0, 0.0001);
    }

    [Fact]
    public void CalculateNdcgAt10_WhenInvertedRanking_ShouldBeStrictlyLessThan1()
    {
        // Retrieved in reverse order: lowest gain first
        var retrieved = new List<string> { "doc3", "doc2", "doc1" };
        var groundTruth = new Dictionary<string, int>
        {
            ["doc1"] = 3,
            ["doc2"] = 2,
            ["doc3"] = 1
        };

        var ndcg = SearchMetrics.CalculateNdcgAt10(retrieved, groundTruth);
        ndcg.Should().BeLessThan(1.0);
        ndcg.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public void CalculateNdcgAt10_WhenNoItemsRetrieved_ShouldEqual0()
    {
        var retrieved = new List<string>();
        var groundTruth = new Dictionary<string, int> { ["doc1"] = 3 };

        var ndcg = SearchMetrics.CalculateNdcgAt10(retrieved, groundTruth);
        ndcg.Should().Be(0.0);
    }

    [Fact]
    public void CalculateRecallAt20_WhenAllRelevantRetrievedInTop20_ShouldEqual1()
    {
        var retrieved = new List<string> { "doc1", "doc2" };
        var groundTruth = new Dictionary<string, int>
        {
            ["doc1"] = 3, // relevant (>= 2)
            ["doc2"] = 2, // relevant (>= 2)
            ["doc3"] = 1  // marginal (< 2, not counted in relevant denominator)
        };

        var recall = SearchMetrics.CalculateRecallAt20(retrieved, groundTruth);
        recall.Should().Be(1.0);
    }

    [Fact]
    public void CalculateRecallAt20_WhenHalfRelevantRetrieved_ShouldEqualHalf()
    {
        var retrieved = new List<string> { "doc1" };
        var groundTruth = new Dictionary<string, int>
        {
            ["doc1"] = 3,
            ["doc2"] = 2
        };

        var recall = SearchMetrics.CalculateRecallAt20(retrieved, groundTruth);
        recall.Should().BeApproximately(0.5, 0.0001);
    }

    [Fact]
    public void CalculateMrr_WhenFirstRelevantAtRank1_ShouldEqual1()
    {
        var retrieved = new List<string> { "doc1", "doc2" };
        var groundTruth = new Dictionary<string, int> { ["doc1"] = 3, ["doc2"] = 2 };

        var mrr = SearchMetrics.CalculateMrr(retrieved, groundTruth);
        mrr.Should().Be(1.0);
    }

    [Fact]
    public void CalculateMrr_WhenFirstRelevantAtRank2_ShouldEqualHalf()
    {
        var retrieved = new List<string> { "docIrrelevant", "docRelevant" };
        var groundTruth = new Dictionary<string, int>
        {
            ["docIrrelevant"] = 0,
            ["docRelevant"] = 2
        };

        var mrr = SearchMetrics.CalculateMrr(retrieved, groundTruth);
        mrr.Should().BeApproximately(0.5, 0.0001);
    }

    [Fact]
    public void CalculateMrr_WhenNoRelevantItemRetrieved_ShouldEqual0()
    {
        var retrieved = new List<string> { "doc1", "doc2" };
        var groundTruth = new Dictionary<string, int>
        {
            ["doc1"] = 0,
            ["doc2"] = 1 // grade 1 is marginal, threshold is >= 2
        };

        var mrr = SearchMetrics.CalculateMrr(retrieved, groundTruth);
        mrr.Should().Be(0.0);
    }

    [Fact]
    public void CalculateMacroAverage_ShouldComputeArithmeticMeanAcrossQueries()
    {
        var queryMetrics = new List<QueryEvaluationMetrics>
        {
            new("q1", "uk", "natural", 1.0, 1.0, 1.0),
            new("q2", "en", "natural", 0.6, 0.5, 0.5)
        };

        MacroMetricsSummary macro = SearchMetrics.CalculateMacroAverage(queryMetrics);
        macro.QueryCount.Should().Be(2);
        macro.MeanNdcgAt10.Should().BeApproximately(0.8, 0.0001);
        macro.MeanRecallAt20.Should().BeApproximately(0.75, 0.0001);
        macro.MeanMrr.Should().BeApproximately(0.75, 0.0001);
    }

    [Fact]
    public void TrackedDatasetV1_ShouldPassValidationStrictly()
    {
        // Locate dataset.v1.json relative to tests directory
        var basePath = AppContext.BaseDirectory;
        var datasetPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"));

        ValidationResult result = DatasetValidator.ValidateFile(datasetPath);
        result.IsValid.Should().BeTrue($"Dataset validation failed with errors: {string.Join("; ", result.Errors)}");
        result.Dataset.Should().NotBeNull();
        result.Dataset!.Documents.Count.Should().BeGreaterThanOrEqualTo(60);
        result.Dataset.Queries.Count.Should().BeGreaterThanOrEqualTo(90);
    }

    [Fact]
    public void ValidateString_WhenUnknownFieldInJson_ShouldFail()
    {
        var basePath = AppContext.BaseDirectory;
        var datasetPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"));
        var schemaPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.schema.json"));

        var json = File.ReadAllText(datasetPath);
        // Inject an unknown property at root
        var modifiedJson = json.Insert(json.IndexOf('{') + 1, "\"unexpectedField\": true,");

        ValidationResult result = DatasetValidator.ValidateString(modifiedJson, schemaPath);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("unexpectedField"));
    }

    [Fact]
    public void ValidateString_WhenDuplicateJudgmentDocumentKey_ShouldFail()
    {
        var basePath = AppContext.BaseDirectory;
        var datasetPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"));
        var schemaPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.schema.json"));

        var json = File.ReadAllText(datasetPath);
        // Duplicate the first judgment in the first query
        var targetStr = "\"judgments\": [";
        var insertIdx = json.IndexOf(targetStr, StringComparison.Ordinal) + targetStr.Length;
        var modifiedJson = json.Insert(insertIdx, "{\"documentKey\": \"asset-001\", \"relevance\": 2},");

        ValidationResult result = DatasetValidator.ValidateString(modifiedJson, schemaPath);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("duplicate judgment"));
    }

    [Fact]
    public void ValidateString_WhenQueryContainsControlCharacter_ShouldFail()
    {
        var basePath = AppContext.BaseDirectory;
        var datasetPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"));
        var schemaPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.schema.json"));

        var json = File.ReadAllText(datasetPath);
        // Replace first query text with a string containing a null byte
        var targetStr = "\"text\": \"";
        var idx = json.IndexOf(targetStr, StringComparison.Ordinal) + targetStr.Length;
        var endIdx = json.IndexOf('"', idx);
        var modifiedJson = json[..idx] + "bad\\u0000query" + json[endIdx..];

        ValidationResult result = DatasetValidator.ValidateString(modifiedJson, schemaPath);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("control"));
    }

    [Fact]
    public void ValidateString_WhenQueryExceeds256Scalars_ShouldFail()
    {
        var basePath = AppContext.BaseDirectory;
        var datasetPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.v1.json"));
        var schemaPath = Path.GetFullPath(Path.Combine(basePath, "..", "..", "..", "..", "search-evaluation", "dataset.schema.json"));

        var json = File.ReadAllText(datasetPath);
        var targetStr = "\"text\": \"";
        var idx = json.IndexOf(targetStr, StringComparison.Ordinal) + targetStr.Length;
        var endIdx = json.IndexOf('"', idx);
        var longText = new string('A', 257);
        var modifiedJson = json[..idx] + longText + json[endIdx..];

        ValidationResult result = DatasetValidator.ValidateString(modifiedJson, schemaPath);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("256"));
    }
}
