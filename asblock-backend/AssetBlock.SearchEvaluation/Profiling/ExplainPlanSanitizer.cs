using System.Text.Json;

namespace AssetBlock.SearchEvaluation.Profiling;

public static class ExplainPlanSanitizer
{
    public static SanitizedExplainPlan Sanitize(string explainJson)
    {
        using var doc = JsonDocument.Parse(explainJson);
        JsonElement root = doc.RootElement;

        JsonElement planContainer;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            planContainer = root[0];
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            planContainer = root;
        }
        else
        {
            throw new FormatException("Invalid EXPLAIN JSON structure: expected array or object root.");
        }

        var planningTime = planContainer.TryGetProperty("Planning Time", out JsonElement pt) ? pt.GetDouble() : 0.0;
        var executionTime = planContainer.TryGetProperty("Execution Time", out JsonElement et) ? et.GetDouble() : 0.0;

        if (!planContainer.TryGetProperty("Plan", out JsonElement planElement))
        {
            throw new FormatException("Invalid EXPLAIN JSON: missing 'Plan' element.");
        }

        SanitizedPlanNode rootNode = ParseNode(planElement);
        return new SanitizedExplainPlan(planningTime, executionTime, rootNode);
    }

    private static SanitizedPlanNode ParseNode(JsonElement el)
    {
        var nodeType = el.TryGetProperty("Node Type", out JsonElement nt) ? nt.GetString() ?? "Unknown" : "Unknown";
        var relationName = el.TryGetProperty("Relation Name", out JsonElement rn) ? rn.GetString() : null;
        var indexName = el.TryGetProperty("Index Name", out JsonElement iname) ? iname.GetString() : null;
        var parentRel = el.TryGetProperty("Parent Relationship", out JsonElement pr) ? pr.GetString() : null;
        var joinType = el.TryGetProperty("Join Type", out JsonElement jt) ? jt.GetString() : null;
        var sortMethod = el.TryGetProperty("Sort Method", out JsonElement sm) ? sm.GetString() : null;
        var sortSpaceType = el.TryGetProperty("Sort Space Type", out JsonElement sst) ? sst.GetString() : null;

        long? sortSpaceUsedKb = el.TryGetProperty("Sort Space Used", out JsonElement ssu) && ssu.TryGetInt64(out var ssuVal) ? ssuVal : null;
        long? peakMemoryUsageKb = el.TryGetProperty("Peak Memory Usage", out JsonElement pmu) && pmu.TryGetInt64(out var pmuVal) ? pmuVal : null;

        bool? parallelAware = el.TryGetProperty("Parallel Aware", out JsonElement pa) ? pa.GetBoolean() : null;
        bool? asyncCapable = el.TryGetProperty("Async Capable", out JsonElement ac) ? ac.GetBoolean() : null;

        var startupCost = el.TryGetProperty("Startup Cost", out JsonElement sc) ? sc.GetDouble() : 0.0;
        var totalCost = el.TryGetProperty("Total Cost", out JsonElement tc) ? tc.GetDouble() : 0.0;
        var planRows = el.TryGetProperty("Plan Rows", out JsonElement prw) ? prw.GetDouble() : 0.0;
        var planWidth = el.TryGetProperty("Plan Width", out JsonElement pw) ? pw.GetInt32() : 0;

        var actualStartupTime = el.TryGetProperty("Actual Startup Time", out JsonElement ast) ? ast.GetDouble() : 0.0;
        var actualTotalTime = el.TryGetProperty("Actual Total Time", out JsonElement att) ? att.GetDouble() : 0.0;
        var actualRows = el.TryGetProperty("Actual Rows", out JsonElement ar) ? ar.GetDouble() : 0.0;
        var actualLoops = el.TryGetProperty("Actual Loops", out JsonElement al) ? al.GetDouble() : 0.0;

        var sharedHitBlocks = el.TryGetProperty("Shared Hit Blocks", out JsonElement shb) ? shb.GetInt64() : 0;
        var sharedReadBlocks = el.TryGetProperty("Shared Read Blocks", out JsonElement srb) ? srb.GetInt64() : 0;
        var sharedDirtiedBlocks = el.TryGetProperty("Shared Dirtied Blocks", out JsonElement sdb) ? sdb.GetInt64() : 0;
        var sharedWrittenBlocks = el.TryGetProperty("Shared Written Blocks", out JsonElement swb) ? swb.GetInt64() : 0;
        var tempReadBlocks = el.TryGetProperty("Temp Read Blocks", out JsonElement trb) ? trb.GetInt64() : 0;
        var tempWrittenBlocks = el.TryGetProperty("Temp Written Blocks", out JsonElement twb) ? twb.GetInt64() : 0;

        var childPlans = new List<SanitizedPlanNode>();
        if (el.TryGetProperty("Plans", out JsonElement plans) && plans.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in plans.EnumerateArray())
            {
                childPlans.Add(ParseNode(child));
            }
        }

        return new SanitizedPlanNode(
            nodeType,
            relationName,
            indexName,
            parentRel,
            joinType,
            sortMethod,
            sortSpaceType,
            sortSpaceUsedKb,
            peakMemoryUsageKb,
            parallelAware,
            asyncCapable,
            startupCost,
            totalCost,
            planRows,
            planWidth,
            actualStartupTime,
            actualTotalTime,
            actualRows,
            actualLoops,
            sharedHitBlocks,
            sharedReadBlocks,
            sharedDirtiedBlocks,
            sharedWrittenBlocks,
            tempReadBlocks,
            tempWrittenBlocks,
            childPlans);
    }
}
