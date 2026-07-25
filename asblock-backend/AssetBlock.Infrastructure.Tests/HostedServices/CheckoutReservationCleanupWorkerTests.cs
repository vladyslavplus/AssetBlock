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
    private static (CheckoutReservationCleanupWorker Worker, ServiceProvider Provider) BuildWorker(
        ICheckoutIntentStore checkoutStore,
        IPaymentService paymentService,
        ICheckoutCompletionService? completionService = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => checkoutStore);
        services.AddScoped(_ => paymentService);
        services.AddScoped(_ => completionService ?? Substitute.For<ICheckoutCompletionService>());
        var provider = services.BuildServiceProvider();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        var worker = new CheckoutReservationCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<CheckoutReservationCleanupWorker>.Instance);

        return (worker, provider);
    }

    [Fact]
    public async Task RunCleanup_WhenAttachedIntentStripeReportsExpired_ShouldCancelAndRelease()
    {
        var store = Substitute.For<ICheckoutIntentStore>();
        var payment = Substitute.For<IPaymentService>();
        var intentId = Guid.NewGuid();
        const string sessionId = "cs_expired_test";

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ListExpiredAttachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (intentId, sessionId) });
        payment.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(sessionId, StripeConstants.CheckoutSessionStatuses.EXPIRED, null));
        store.TryCancelAndRelease(intentId, Arg.Any<CancellationToken>()).Returns(true);

        var (sut, provider) = BuildWorker(store, payment);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await store.Received(1).TryCancelAndRelease(intentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenAttachedIntentStripeReportsOpen_ShouldNotCancel()
    {
        var store = Substitute.For<ICheckoutIntentStore>();
        var payment = Substitute.For<IPaymentService>();
        var intentId = Guid.NewGuid();
        const string sessionId = "cs_open_test";

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ListExpiredAttachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (intentId, sessionId) });
        payment.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(sessionId, StripeConstants.CheckoutSessionStatuses.OPEN, null));

        var (sut, provider) = BuildWorker(store, payment);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await store.DidNotReceive().TryCancelAndRelease(intentId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenAttachedIntentStripeReportsComplete_ShouldNotCancel()
    {
        var store = Substitute.For<ICheckoutIntentStore>();
        var payment = Substitute.For<IPaymentService>();
        var completion = Substitute.For<ICheckoutCompletionService>();
        var intentId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string sessionId = "cs_complete_test";
        var completed = new StripeCheckoutCompleted(intentId, userId, sessionId, 10m, "usd");

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ListExpiredAttachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)> { (intentId, sessionId) });
        payment.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(
                sessionId,
                StripeConstants.CheckoutSessionStatuses.COMPLETE,
                null,
                completed));
        completion.CompletePaidCheckout(completed, Arg.Any<CancellationToken>())
            .Returns((OrderCompletedPayload?)null);

        var (sut, provider) = BuildWorker(store, payment, completion);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await store.DidNotReceive().TryCancelAndRelease(intentId, Arg.Any<CancellationToken>());
        await completion.Received(1).CompletePaidCheckout(completed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenNoExpiredIntents_ShouldNotCallPaymentService()
    {
        var store = Substitute.For<ICheckoutIntentStore>();
        var payment = Substitute.For<IPaymentService>();

        store.CleanupExpiredUnattachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);
        store.ListExpiredAttachedPendingBatch(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<(Guid, string)>());

        var (sut, provider) = BuildWorker(store, payment);
        await using (provider)
        {
            await sut.RunCleanup(CancellationToken.None);
        }

        await payment.DidNotReceiveWithAnyArgs().GetCheckoutSession(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
