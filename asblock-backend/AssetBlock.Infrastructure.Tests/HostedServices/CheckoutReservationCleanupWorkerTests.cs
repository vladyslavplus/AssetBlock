using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class CheckoutReservationCleanupWorkerTests
{
    [Fact]
    public void CalculateInitialDelay_WithJitter_ShouldScaleAccurately()
    {
        // Initial delay base is 30s
        CheckoutReservationCleanupWorker.CalculateInitialDelay(() => 0.0).Should().Be(TimeSpan.FromSeconds(24));
        CheckoutReservationCleanupWorker.CalculateInitialDelay(() => 0.5).Should().Be(TimeSpan.FromSeconds(30));
        CheckoutReservationCleanupWorker.CalculateInitialDelay(() => 1.0).Should().Be(TimeSpan.FromSeconds(36));
    }

    [Fact]
    public void CalculateIntervalDelay_WithJitter_ShouldScaleAccurately()
    {
        // Interval base is 1 minute (60s)
        CheckoutReservationCleanupWorker.CalculateIntervalDelay(() => 0.0).Should().Be(TimeSpan.FromSeconds(48));
        CheckoutReservationCleanupWorker.CalculateIntervalDelay(() => 0.5).Should().Be(TimeSpan.FromSeconds(60));
        CheckoutReservationCleanupWorker.CalculateIntervalDelay(() => 1.0).Should().Be(TimeSpan.FromSeconds(72));
    }

    private static (CheckoutReservationCleanupWorker Worker, ServiceProvider Provider) BuildWorker(
        ICheckoutIntentStore checkoutStore,
        IPaymentService paymentService,
        ICheckoutCompletionService? completionService = null,
        TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => checkoutStore);
        services.AddScoped(_ => paymentService);
        services.AddScoped(_ => completionService ?? Substitute.For<ICheckoutCompletionService>());
        ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        var worker = new CheckoutReservationCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<CheckoutReservationCleanupWorker>.Instance,
            timeProvider ?? TimeProvider.System);

        return (worker, provider);
    }

    [Fact]
    public async Task RunCleanup_PassesDeterministicCutoffsFromTimeProvider()
    {
        var fixedTime = new DateTimeOffset(2026, 9, 4, 15, 30, 0, TimeSpan.Zero);
        var timeProvider = new ControllableTimeProvider(fixedTime);
        ICheckoutIntentStore store = Substitute.For<ICheckoutIntentStore>();
        IPaymentService payment = Substitute.For<IPaymentService>();

        (CheckoutReservationCleanupWorker sut, ServiceProvider provider) = BuildWorker(store, payment, timeProvider: timeProvider);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        DateTimeOffset expectedSyncCutoff = fixedTime - TimeSpan.FromMinutes(2);
        await store.Received(1).CleanupExpiredUnattachedPendingBatch(fixedTime, 100, Arg.Any<CancellationToken>());
        await store.Received(1).ClaimAttachedPendingForStripeSyncBatch(fixedTime, expectedSyncCutoff, 100, Arg.Any<CancellationToken>());
    }

    private sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task RunCleanup_WhenAttachedIntentStripeReportsExpired_ShouldCancelAndRelease()
    {
        ICheckoutIntentStore store = Substitute.For<ICheckoutIntentStore>();
        IPaymentService payment = Substitute.For<IPaymentService>();
        var intentId = Guid.NewGuid();
        const string sessionId = "cs_expired_test";

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ClaimAttachedPendingForStripeSyncBatch(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (intentId, sessionId) });
        payment.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(sessionId, StripeConstants.CheckoutSessionStatuses.EXPIRED, null));
        store.TryCancelAndRelease(intentId, Arg.Any<CancellationToken>()).Returns(true);

        (CheckoutReservationCleanupWorker? sut, ServiceProvider? provider) = BuildWorker(store, payment);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await store.Received(1).TryCancelAndRelease(intentId, Arg.Any<CancellationToken>());
        await store.DidNotReceive().TouchLastStripeReconciledAt(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenAttachedIntentStripeReportsOpen_ShouldNotCancel()
    {
        ICheckoutIntentStore store = Substitute.For<ICheckoutIntentStore>();
        IPaymentService payment = Substitute.For<IPaymentService>();
        var intentId = Guid.NewGuid();
        const string sessionId = "cs_open_test";

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ClaimAttachedPendingForStripeSyncBatch(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (intentId, sessionId) });
        payment.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(sessionId, StripeConstants.CheckoutSessionStatuses.OPEN, null));

        (CheckoutReservationCleanupWorker? sut, ServiceProvider? provider) = BuildWorker(store, payment);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await store.DidNotReceive().TryCancelAndRelease(intentId, Arg.Any<CancellationToken>());
        // Lease already applied inside ClaimAttachedPendingForStripeSyncBatch.
        await store.DidNotReceive().TouchLastStripeReconciledAt(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenAttachedPaidSessionWithoutWebhook_ShouldCompleteViaReconciliation()
    {
        ICheckoutIntentStore store = Substitute.For<ICheckoutIntentStore>();
        IPaymentService payment = Substitute.For<IPaymentService>();
        ICheckoutCompletionService completion = Substitute.For<ICheckoutCompletionService>();
        var intentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string sessionId = "cs_complete_early_test";
        var completed = new StripeCheckoutCompleted(intentId, userId, sessionId, 10m, "usd");

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ClaimAttachedPendingForStripeSyncBatch(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (intentId, sessionId) });
        payment.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(
                sessionId,
                StripeConstants.CheckoutSessionStatuses.COMPLETE,
                null,
                completed));
        completion.CompletePaidCheckout(completed, Arg.Any<CancellationToken>())
            .Returns((OrderCompletedPayload?)null);

        (CheckoutReservationCleanupWorker? sut, ServiceProvider? provider) = BuildWorker(store, payment, completion);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await store.DidNotReceive().TryCancelAndRelease(intentId, Arg.Any<CancellationToken>());
        await completion.Received(1).CompletePaidCheckout(completed, Arg.Any<CancellationToken>());
        await store.DidNotReceive().TouchLastStripeReconciledAt(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenNoDueAttachedIntents_ShouldNotCallPaymentService()
    {
        ICheckoutIntentStore store = Substitute.For<ICheckoutIntentStore>();
        IPaymentService payment = Substitute.For<IPaymentService>();

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ClaimAttachedPendingForStripeSyncBatch(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)>());

        (CheckoutReservationCleanupWorker? sut, ServiceProvider? provider) = BuildWorker(store, payment);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await payment.DidNotReceiveWithAnyArgs().GetCheckoutSession(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}

public sealed class RefreshTokenRetentionWorkerTests
{
    [Fact]
    public void CalculateInitialDelay_WithJitter_ShouldScaleAccurately()
    {
        // Initial delay base is 2 minutes (120s)
        RefreshTokenRetentionWorker.CalculateInitialDelay(() => 0.0).Should().Be(TimeSpan.FromSeconds(96));
        RefreshTokenRetentionWorker.CalculateInitialDelay(() => 0.5).Should().Be(TimeSpan.FromSeconds(120));
        RefreshTokenRetentionWorker.CalculateInitialDelay(() => 1.0).Should().Be(TimeSpan.FromSeconds(144));
    }

    [Fact]
    public void CalculateIntervalDelay_WithJitter_ShouldScaleAccurately()
    {
        // Interval base is 1 hour (3600s)
        RefreshTokenRetentionWorker.CalculateIntervalDelay(() => 0.0).Should().Be(TimeSpan.FromMinutes(48));
        RefreshTokenRetentionWorker.CalculateIntervalDelay(() => 0.5).Should().Be(TimeSpan.FromMinutes(60));
        RefreshTokenRetentionWorker.CalculateIntervalDelay(() => 1.0).Should().Be(TimeSpan.FromMinutes(72));
    }

    [Fact]
    public async Task RunCleanup_WhenTokensExpired_ShouldCallCleanupExpiredTokensInBatches()
    {
        IJwtTokenService jwtTokenService = Substitute.For<IJwtTokenService>();
        jwtTokenService.CleanupExpiredTokens(Arg.Any<DateTimeOffset>(), 500, Arg.Any<CancellationToken>())
            .Returns(500, 120);

        var services = new ServiceCollection();
        services.AddScoped(_ => jwtTokenService);
        ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        var sut = new RefreshTokenRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<RefreshTokenRetentionWorker>.Instance);

        await using (provider)
        {
            var deleted = await sut.RunCleanup(CancellationToken.None);
            deleted.Should().Be(620);
        }

        await jwtTokenService.Received(2).CleanupExpiredTokens(Arg.Any<DateTimeOffset>(), 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenNoExpiredTokens_ShouldCallOnceAndReturnZero()
    {
        IJwtTokenService jwtTokenService = Substitute.For<IJwtTokenService>();
        jwtTokenService.CleanupExpiredTokens(Arg.Any<DateTimeOffset>(), 500, Arg.Any<CancellationToken>())
            .Returns(0);

        var services = new ServiceCollection();
        services.AddScoped(_ => jwtTokenService);
        ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        var sut = new RefreshTokenRetentionWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<RefreshTokenRetentionWorker>.Instance);

        await using (provider)
        {
            var deleted = await sut.RunCleanup(CancellationToken.None);
            deleted.Should().Be(0);
        }

        await jwtTokenService.Received(1).CleanupExpiredTokens(Arg.Any<DateTimeOffset>(), 500, Arg.Any<CancellationToken>());
    }
}
