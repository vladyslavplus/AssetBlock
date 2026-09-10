using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace AssetBlock.SearchEvaluation.Profiling;

public sealed record CapturedDbCommand(
    string CommandText,
    List<NpgsqlParameter> Parameters);

public sealed class SqlProfilingInterceptor : DbCommandInterceptor
{
    private readonly List<CapturedDbCommand> _captured = [];
    private readonly Lock _lock = new();

    private bool IsCapturing { get; set; } = true;

    public IReadOnlyList<CapturedDbCommand> GetCapturedCommands()
    {
        lock (_lock)
        {
            return [.. _captured];
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _captured.Clear();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        CaptureCommand(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        CaptureCommand(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public void CaptureCommand(DbCommand command)
    {
        if (!IsCapturing)
        {
            return;
        }

        var clonedParameters = new List<NpgsqlParameter>(command.Parameters.Count);
        foreach (DbParameter p in command.Parameters)
        {
            var clone = new NpgsqlParameter
            {
                ParameterName = p.ParameterName,
                Value = p.Value,
                DbType = p.DbType,
                Direction = p.Direction
            };

            if (p is NpgsqlParameter np)
            {
                if (np.NpgsqlDbType != NpgsqlTypes.NpgsqlDbType.Unknown)
                {
                    clone.NpgsqlDbType = np.NpgsqlDbType;
                }
                clone.DataTypeName = np.DataTypeName;
            }

            clonedParameters.Add(clone);
        }

        lock (_lock)
        {
            _captured.Add(new CapturedDbCommand(command.CommandText, clonedParameters));
        }
    }

    public static async Task<SanitizedExplainPlan> ReplayExplainAsync(
        NpgsqlConnection connection,
        CapturedDbCommand capturedCommand,
        CancellationToken cancellationToken,
        int timeoutSeconds = 30)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using NpgsqlCommand explainCmd = connection.CreateCommand();
#pragma warning disable CA2100 // Evaluation replay of intercepted internal EF Core parameterized command
        explainCmd.CommandText = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {capturedCommand.CommandText}";
#pragma warning restore CA2100
        explainCmd.CommandTimeout = timeoutSeconds;

        foreach (NpgsqlParameter p in capturedCommand.Parameters)
        {
            var copy = new NpgsqlParameter
            {
                ParameterName = p.ParameterName,
                Value = p.Value,
                DbType = p.DbType,
                DataTypeName = p.DataTypeName,
                Direction = p.Direction
            };
            if (p.NpgsqlDbType != NpgsqlTypes.NpgsqlDbType.Unknown)
            {
                copy.NpgsqlDbType = p.NpgsqlDbType;
            }
            explainCmd.Parameters.Add(copy);
        }

        await using DbDataReader reader = await explainCmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("EXPLAIN command did not return any rows.");
        }

        var rawJson = reader.GetString(0);
        return ExplainPlanSanitizer.Sanitize(rawJson);
    }
}
