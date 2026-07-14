namespace AssetBlock.Infrastructure.IntegrationTests.Support;

[CollectionDefinition(nameof(PostgresStoreCollection), DisableParallelization = true)]
public sealed class PostgresStoreCollection : ICollectionFixture<PostgresFixture>;
