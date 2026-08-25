using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Ai;
using AssetBlock.Infrastructure.Observability;
using AssetBlock.Infrastructure.Tests.Observability;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AssetBlock.Infrastructure.Tests.Ai;

[Collection(AssetBlockDiagnosticsCollection.NAME)]
public sealed class AiTelemetryTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ActivityListener _activityListener;
    private readonly List<(string Name, KeyValuePair<string, object?>[] Tags)> _records = [];

    public AiTelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AssetBlockDiagnostics.ACTIVITY_SOURCE_NAME,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_activityListener);

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AssetBlockDiagnostics.METER_NAME)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            _records.Add((instrument.Name, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            _records.Add((instrument.Name, tags.ToArray())));
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _activityListener.Dispose();
    }

    [Fact]
    public void Record_ShouldWriteRequestIdOnActivityNotMetrics()
    {
        using var activity = AssetBlockDiagnostics.ActivitySource.StartActivity("test");
        var sut = new AiTelemetry();

        sut.Record(
            AiProviderKind.OPENROUTER,
            null,
            AiTelemetryOutcome.SUCCESS,
            TimeSpan.FromMilliseconds(10),
            3,
            2,
            "gen-secret-not-a-metric");

        activity!.GetTagItem(AiTelemetry.REQUEST_ID_TAG).Should().Be("gen-secret-not-a-metric");
        var metricTags = _records.SelectMany(r => r.Tags.Select(t => t.Key)).Distinct();
        metricTags.Should().NotContain("ai.request_id");
        metricTags.Should().NotContain("gen-secret-not-a-metric");
        _records.Where(r => r.Name is "assetblock.ai.requests" or "assetblock.ai.results").Should().HaveCount(2);
        _records.First(r => r.Name == "assetblock.ai.results").Tags.Should()
            .Contain(t => t.Key == "ai.model" && (string?)t.Value == AiTelemetry.UNKNOWN_MODEL);
    }
}
