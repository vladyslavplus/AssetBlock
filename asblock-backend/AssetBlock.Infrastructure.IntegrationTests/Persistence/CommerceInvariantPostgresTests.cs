using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CommerceInvariantPostgresTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("USD")]
    [InlineData("Usd")]
    [InlineData("us")]
    [InlineData("usdd")]
    [InlineData("123")]
    public async Task CheckoutIntent_WhenCurrencyNotLowercaseUsd_ShouldViolateCheckConstraint(string currency)
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("cur-buyer", "cur-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Currency Gate", price: 5m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = Guid.NewGuid(),
            UserId = buyer.Id,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = asset.Price,
            Currency = currency,
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        var act = () => db.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        // Longer-than-3 values hit varchar(3) first (22001); invalid 3-letter codes hit check (23514).
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().BeOneOf(
                PostgresErrorCodes.CheckViolation,
                PostgresErrorCodes.StringDataRightTruncation);
    }

    [Fact]
    public async Task CheckoutIntentItem_WhenAssetVersionBelongsToOtherAsset_ShouldViolateForeignKey()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("mismatch-buyer", "mismatch-buyer@example.test");
        db.Users.Add(buyer);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Asset A", price: 5m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Asset B", price: 8m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.Add(versionB);
        await db.SaveChangesAsync();

        var intentId = Guid.NewGuid();
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            AssetId = assetA.Id,
            ProductTitle = assetA.Title,
            AmountTotal = assetA.Price,
            Currency = "usd",
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        db.CheckoutIntentItems.Add(new CheckoutIntentItem
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = assetA.Id,
            AssetVersionId = versionB.Id,
            SellerId = author.Id,
            Position = 1,
            AssetTitleSnapshot = assetA.Title,
            VersionNumber = 1,
            ListPrice = assetA.Price,
            AllocatedPrice = assetA.Price,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });

        var act = () => db.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task OrderLine_WhenAssetVersionBelongsToOtherAsset_ShouldViolateForeignKey()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("oline-buyer", "oline-buyer@example.test");
        db.Users.Add(buyer);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Line A", price: 5m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Line B", price: 8m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        var versionA = TestData.CreateAssetVersion(assetA.Id);
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            AssetId = assetA.Id,
            ProductTitle = assetA.Title,
            AmountTotal = assetA.Price,
            Currency = "usd",
            StripeSessionId = "cs_mismatch_order_line",
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            CompletedAt = DateTimeOffset.UtcNow
        });
        db.CheckoutIntentItems.Add(new CheckoutIntentItem
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = assetA.Id,
            AssetVersionId = versionA.Id,
            SellerId = author.Id,
            Position = 1,
            AssetTitleSnapshot = assetA.Title,
            VersionNumber = 1,
            ListPrice = assetA.Price,
            AllocatedPrice = assetA.Price,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });
        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = buyer.Id,
            CheckoutIntentId = intentId,
            AssetId = assetA.Id,
            ProductTitle = assetA.Title,
            StripeSessionId = "cs_mismatch_order_line",
            AmountPaid = assetA.Price,
            Currency = "usd",
            PurchasedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        db.OrderLines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AssetId = assetA.Id,
            AssetVersionId = versionB.Id,
            SellerId = author.Id,
            Position = 1,
            AssetTitleSnapshot = assetA.Title,
            VersionNumber = 1,
            ListPrice = assetA.Price,
            PricePaid = assetA.Price,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });

        var act = () => db.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        ex.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldContainCurrencyAndAssetVersionInvariants()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var checks = await db.Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE contype = 'c'
                  AND conname IN (
                    'CK_checkout_intents_currency_iso_lower',
                    'CK_checkout_intents_currency_usd_v1',
                    'CK_orders_currency_iso_lower',
                    'CK_orders_currency_usd_v1',
                    'CK_bundle_revisions_currency_iso_lower',
                    'CK_bundle_revisions_currency_usd_v1'
                  )
                """)
            .ToListAsync();

        checks.Should().HaveCount(6);

        var hasAlternateKey = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_constraint
                    WHERE conname = 'AK_asset_versions_AssetId_Id'
                      AND contype = 'u'
                ) AS "Value"
                """)
            .SingleAsync();
        hasAlternateKey.Should().BeTrue();
    }
}
