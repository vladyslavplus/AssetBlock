using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AssetBlock.WebApi.IntegrationTests.Support;

internal sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "AssetBlock.WebApi.IntegrationTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
