namespace AssetBlock.WebApi.IntegrationTests.Support;

[CollectionDefinition(nameof(IntegrationTestCollection))]
public sealed class IntegrationTestCollection : ICollectionFixture<IntegrationTestFixture>;
