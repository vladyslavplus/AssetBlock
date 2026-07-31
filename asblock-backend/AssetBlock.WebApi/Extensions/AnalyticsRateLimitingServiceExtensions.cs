using AssetBlock.WebApi.Middleware;
using AssetBlock.WebApi.Services;

namespace AssetBlock.WebApi.Extensions;

internal static class AnalyticsRateLimitingServiceExtensions
{
    extension(IServiceCollection services)
    {
        public void AddAnalyticsBffSignatureValidation()
        {
            services.AddSingleton<IAnalyticsBffSignatureValidator, AnalyticsBffSignatureValidator>();
        }
    }

    extension(IApplicationBuilder app)
    {
        public void UseAnalyticsBffSignatureValidation()
        {
            app.UseMiddleware<AnalyticsBffSignatureMiddleware>();
        }
    }
}
