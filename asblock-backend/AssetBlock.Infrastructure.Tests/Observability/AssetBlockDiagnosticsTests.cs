using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Observability;
using System.Diagnostics.Metrics;

namespace AssetBlock.Infrastructure.Tests.Observability;

[Collection(AssetBlockDiagnosticsCollection.NAME)]
public sealed class AssetBlockDiagnosticsTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly List<(Instrument Instrument, object? Measurement, KeyValuePair<string, object?>[] Tags)> _recordedMeasurements;

    public AssetBlockDiagnosticsTests()
    {
        _recordedMeasurements = [];
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

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            _recordedMeasurements.Add((instrument, measurement, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _recordedMeasurements.Add((instrument, measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose()
    {
        AssetBlockDiagnostics.TimeProvider = TimeProvider.System;
        _listener.Dispose();
    }

    private IEnumerable<(Instrument Instrument, object? Measurement, Dictionary<string, object?> Tags)> GetMeasurements(string instrumentName)
    {
        _listener.RecordObservableInstruments();
        return _recordedMeasurements
            .Where(m => m.Instrument.Name == instrumentName)
            .Select(m => (m.Instrument, m.Measurement, m.Tags.ToDictionary(t => t.Key, t => t.Value)));
    }

    [Fact]
    public void RecordOutboxProcessing_ShouldRecordDurationAndCount()
    {
        AssetBlockDiagnostics.RecordOutboxProcessing(TimeSpan.FromSeconds(2.5), "TestType", DiagnosticsOutcome.SUCCESS);

        var durations = GetMeasurements("assetblock.outbox.processing.duration").ToList();
        durations.Should().HaveCount(1);
        durations[0].Instrument.Unit.Should().Be("s");
        durations[0].Measurement.Should().Be(2.5);
        durations[0].Tags["outbox.type"].Should().Be("TestType");
        durations[0].Tags["outbox.outcome"].Should().Be("success");

        var counts = GetMeasurements("assetblock.outbox.processing.count").ToList();
        counts.Should().HaveCount(1);
        counts[0].Measurement.Should().Be(1L);
        counts[0].Tags["outbox.type"].Should().Be("TestType");
        counts[0].Tags["outbox.outcome"].Should().Be("success");
    }

    [Fact]
    public void RecordAnalyticsAggregation_ShouldRecordDuration()
    {
        AssetBlockDiagnostics.RecordAnalyticsAggregation(TimeSpan.FromSeconds(3.1), DiagnosticsOutcome.SKIPPED_LOCKED);

        var durations = GetMeasurements("assetblock.analytics.aggregation.duration").ToList();
        durations.Should().HaveCount(1);
        durations[0].Instrument.Unit.Should().Be("s");
        durations[0].Measurement.Should().Be(3.1);
        durations[0].Tags["analytics.outcome"].Should().Be("skipped_locked");
    }

    [Fact]
    public void RecordOrphanCleanup_ShouldRecordDurationAndCounters()
    {
        AssetBlockDiagnostics.RecordOrphanCleanup(TimeSpan.FromSeconds(5.5), DiagnosticsOutcome.PARTIAL_FAILURE, 42, 3);

        var durations = GetMeasurements("assetblock.storage.orphan_cleanup.duration").ToList();
        durations.Should().HaveCount(1);
        durations[0].Instrument.Unit.Should().Be("s");
        durations[0].Measurement.Should().Be(5.5);
        durations[0].Tags["cleanup.outcome"].Should().Be("partial_failure");

        var deleted = GetMeasurements("assetblock.storage.orphan_cleanup.deleted").ToList();
        deleted.Should().HaveCount(1);
        deleted[0].Measurement.Should().Be(42L);
        deleted[0].Tags.Should().BeEmpty();

        var failures = GetMeasurements("assetblock.storage.orphan_cleanup.failures").ToList();
        failures.Should().HaveCount(1);
        failures[0].Measurement.Should().Be(3L);
        failures[0].Tags.Should().BeEmpty();
    }

    [Fact]
    public void RecordOrphanCleanup_WhenZeroCounts_ShouldNotRecordCounters()
    {
        AssetBlockDiagnostics.RecordOrphanCleanup(TimeSpan.FromSeconds(1.0), DiagnosticsOutcome.FAILURE, 0, 0);

        var durations = GetMeasurements("assetblock.storage.orphan_cleanup.duration").ToList();
        durations.Should().HaveCount(1);

        var deleted = GetMeasurements("assetblock.storage.orphan_cleanup.deleted").ToList();
        deleted.Should().BeEmpty();

        var failures = GetMeasurements("assetblock.storage.orphan_cleanup.failures").ToList();
        failures.Should().BeEmpty();
    }

    [Fact]
    public void RecordEmailDispatch_ShouldRecordDuration()
    {
        AssetBlockDiagnostics.RecordEmailDispatch(TimeSpan.FromSeconds(0.5), EmailTemplateKind.EMAIL_VERIFICATION, DiagnosticsOutcome.FAILURE);

        var durations = GetMeasurements("assetblock.email.dispatch.duration").ToList();
        durations.Should().HaveCount(1);
        durations[0].Instrument.Unit.Should().Be("s");
        durations[0].Measurement.Should().Be(0.5);
        durations[0].Tags["email.template"].Should().Be("EMAIL_VERIFICATION");
        durations[0].Tags["email.outcome"].Should().Be("failure");
    }

    [Fact]
    public void ActiveJobs_IncrementAndDecrement_ShouldRecordUpDownCounter()
    {
        AssetBlockDiagnostics.IncrementActiveJobs(AssetProcessingJobType.ARCHIVE_INSPECTION);
        AssetBlockDiagnostics.DecrementActiveJobs(AssetProcessingJobType.ARCHIVE_INSPECTION);

        var measurements = GetMeasurements("assetblock.jobs.active").ToList();
        measurements.Should().HaveCount(2);
        measurements[0].Measurement.Should().Be(1L);
        measurements[0].Tags["job.type"].Should().Be("ARCHIVE_INSPECTION");
        measurements[1].Measurement.Should().Be(-1L);
        measurements[1].Tags["job.type"].Should().Be("ARCHIVE_INSPECTION");
    }

    [Fact]
    public void RecordJobCompletion_ShouldRecordAllMetersWithExpectedTags()
    {
        AssetBlockDiagnostics.RecordJobCompletion(
            AssetProcessingJobType.MALWARE_SCAN,
            JobOutcomeNames.SUCCESS,
            TimeSpan.FromSeconds(3.5),
            TimeSpan.FromSeconds(12.0),
            attemptCount: 2);

        var completed = GetMeasurements("assetblock.jobs.completed").ToList();
        completed.Should().HaveCount(1);
        completed[0].Measurement.Should().Be(1L);
        completed[0].Tags["job.type"].Should().Be("MALWARE_SCAN");
        completed[0].Tags["job.outcome"].Should().Be("SUCCESS");

        var duration = GetMeasurements("assetblock.jobs.duration").ToList();
        duration.Should().HaveCount(1);
        duration[0].Measurement.Should().Be(3.5);
        duration[0].Tags["job.type"].Should().Be("MALWARE_SCAN");
        duration[0].Tags["job.outcome"].Should().Be("SUCCESS");

        var queueAge = GetMeasurements("assetblock.jobs.queue_age").ToList();
        queueAge.Should().HaveCount(1);
        queueAge[0].Measurement.Should().Be(12.0);

        var attempts = GetMeasurements("assetblock.jobs.attempts").ToList();
        attempts.Should().HaveCount(1);
        attempts[0].Measurement.Should().Be(2L);
    }

    [Fact]
    public void RecordScan_ShouldRecordOneDurationCountAndByteKinds()
    {
        AssetBlockDiagnostics.RecordScan(
            TimeSpan.FromSeconds(1.25),
            ScanDiagnosticsOutcome.CLEAN,
            bytesRead: 40,
            bytesAttempted: 32,
            bytesTransferred: 32);

        var durations = GetMeasurements("assetblock.scan.duration").ToList();
        durations.Should().HaveCount(1);
        durations[0].Instrument.Unit.Should().Be("s");
        durations[0].Measurement.Should().Be(1.25);
        durations[0].Tags.Should().ContainKey("scan.outcome");
        durations[0].Tags["scan.outcome"].Should().Be("CLEAN");
        durations[0].Tags.Should().HaveCount(1);

        var results = GetMeasurements("assetblock.scan.results").ToList();
        results.Should().HaveCount(1);
        results[0].Measurement.Should().Be(1L);
        results[0].Tags["scan.outcome"].Should().Be("CLEAN");

        var bytes = GetMeasurements("assetblock.scan.bytes").ToList();
        bytes.Should().HaveCount(3);
        bytes[0].Instrument.Unit.Should().Be("By");
        bytes.Select(b => b.Tags["scan.bytes.kind"]).Should().Equal("READ", "ATTEMPTED", "TRANSFERRED");
        bytes.Select(b => b.Measurement).Should().Equal(40L, 32L, 32L);
        bytes.Should().OnlyContain(b => b.Tags.Keys.Single() == "scan.bytes.kind");
    }

    [Fact]
    public void RecordScan_ShouldMapBoundedOutcomes()
    {
        var cases = new (ScanDiagnosticsOutcome Outcome, string Tag)[]
        {
            (ScanDiagnosticsOutcome.INFECTED, "INFECTED"),
            (ScanDiagnosticsOutcome.LIMIT_EXCEEDED, "LIMIT_EXCEEDED"),
            (ScanDiagnosticsOutcome.UNAVAILABLE, "UNAVAILABLE"),
            (ScanDiagnosticsOutcome.ERROR, "ERROR"),
            (ScanDiagnosticsOutcome.CANCELLED, "CANCELLED")
        };

        foreach ((ScanDiagnosticsOutcome outcome, _) in cases)
        {
            AssetBlockDiagnostics.RecordScan(TimeSpan.FromMilliseconds(10), outcome, 0, 0, 0);
        }

        var results = GetMeasurements("assetblock.scan.results").ToList();
        results.Should().HaveCount(cases.Length);
        results.Select(r => r.Tags["scan.outcome"]).Should().Equal(cases.Select(c => c.Tag));
    }

    [Fact]
    public void ObserveSignatureDatabase_ShouldPublishAgeThatIncreasesWithClock()
    {
        var clock = new ControllableTimeProvider(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        AssetBlockDiagnostics.TimeProvider = clock;
        var builtAt = clock.GetUtcNow().AddHours(-3);
        AssetBlockDiagnostics.ObserveSignatureDatabase(builtAt);

        var first = GetMeasurements("assetblock.scan.signature_age").ToList();
        first.Should().HaveCount(1);
        first[0].Instrument.Unit.Should().Be("s");
        first[0].Measurement.Should().Be(TimeSpan.FromHours(3).TotalSeconds);
        first[0].Tags.Should().BeEmpty();

        clock.Advance(TimeSpan.FromHours(1));

        var second = GetMeasurements("assetblock.scan.signature_age").ToList();
        second.Should().HaveCount(2);
        Convert.ToDouble(second[1].Measurement).Should().Be(TimeSpan.FromHours(4).TotalSeconds);
        Convert.ToDouble(second[1].Measurement).Should().BeGreaterThan(Convert.ToDouble(second[0].Measurement));
    }

    private sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }
}
