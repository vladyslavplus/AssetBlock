using Testcontainers.PostgreSql;

namespace AssetBlock.WebApi.IntegrationTests.Support;

public sealed class IntegrationTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder().Build();

    private AssetBlockWebApplicationFactory? _factory;

    public AssetBlockWebApplicationFactory Factory => _factory!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _factory = new AssetBlockWebApplicationFactory(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_factory != null)
        {
            await _factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }
}
