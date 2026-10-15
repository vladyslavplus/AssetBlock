using System.Diagnostics.CodeAnalysis;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using NpgsqlTypes;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CodeAnalysisFindingCoordinateConstraintsPostgresTests(PostgresFixture fixture)
{
    [Fact]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test uses trusted EF check-constraint expressions and has no request input.")]
    public async Task CoordinateChecks_WhenHalfNullOrInvalid_ShouldRejectAndPreserveValidSpans()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        await db.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        IEntityType finding = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(CodeAnalysisReportFinding))!;
        var bytesCheck = finding.GetCheckConstraints()
            .Single(check => check.Name == "CK_code_analysis_report_findings_spans").Sql;
        var linesCheck = finding.GetCheckConstraints()
            .Single(check => check.Name == "CK_code_analysis_report_findings_lines").Sql;

        List<string> appliedChecks = await db.Database.SqlQueryRaw<string>(
                """
                SELECT pg_get_constraintdef(oid) AS "Value"
                FROM pg_constraint
                WHERE conrelid = 'code_analysis_report_findings'::regclass
                  AND conname IN (
                    'CK_code_analysis_report_findings_spans',
                    'CK_code_analysis_report_findings_lines'
                  )
                """)
            .ToListAsync();
        appliedChecks.Should().HaveCount(2);
        appliedChecks.Should().Contain(definition =>
            definition.Contains("\"OwnStartByte\" IS NOT NULL", StringComparison.Ordinal));
        appliedChecks.Should().Contain(definition =>
            definition.Contains("\"OwnStartLine\" IS NOT NULL", StringComparison.Ordinal));

        await using (NpgsqlCommand create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TEMPORARY TABLE finding_coordinate_probe (
                    "OwnStartByte" integer NULL,
                    "OwnEndByte" integer NULL,
                    "OwnStartLine" integer NULL,
                    "OwnEndLine" integer NULL,
                    CONSTRAINT "ck_probe_bytes" CHECK ({bytesCheck}),
                    CONSTRAINT "ck_probe_lines" CHECK ({linesCheck})
                )
                """;
            await create.ExecuteNonQueryAsync();
        }

        await Insert(connection, null, null, null, null);
        await Insert(connection, 0, 0, 1, 1);
        await Insert(connection, 0, 2, 1, 3);

        await AssertRejected(connection, null, 0, null, null, "ck_probe_bytes");
        await AssertRejected(connection, 0, null, null, null, "ck_probe_bytes");
        await AssertRejected(connection, null, null, null, 1, "ck_probe_lines");
        await AssertRejected(connection, null, null, 1, null, "ck_probe_lines");
        await AssertRejected(connection, -1, 0, null, null, "ck_probe_bytes");
        await AssertRejected(connection, 2, 1, null, null, "ck_probe_bytes");
        await AssertRejected(connection, null, null, 0, 1, "ck_probe_lines");
        await AssertRejected(connection, null, null, 2, 1, "ck_probe_lines");
    }

    private static async Task Insert(
        NpgsqlConnection connection, int? startByte, int? endByte, int? startLine, int? endLine)
    {
        await using NpgsqlCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO finding_coordinate_probe VALUES (@startByte, @endByte, @startLine, @endLine)";
        AddParameter(insert, "startByte", startByte);
        AddParameter(insert, "endByte", endByte);
        AddParameter(insert, "startLine", startLine);
        AddParameter(insert, "endLine", endLine);
        await insert.ExecuteNonQueryAsync();
    }

    private static async Task AssertRejected(
        NpgsqlConnection connection,
        int? startByte,
        int? endByte,
        int? startLine,
        int? endLine,
        string expectedConstraint)
    {
        Func<Task> insert = () => Insert(connection, startByte, endByte, startLine, endLine);
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(insert);
        exception.ConstraintName.Should().Be(expectedConstraint);
    }

    private static void AddParameter(NpgsqlCommand command, string name, int? value)
    {
        object parameterValue = value.HasValue ? value.Value : DBNull.Value;
        command.Parameters.AddWithValue(name, NpgsqlDbType.Integer, parameterValue);
    }
}
