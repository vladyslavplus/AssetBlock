namespace AssetBlock.WebApi.Extensions;

internal static class SwaggerApplicationExtensions
{
    public static IApplicationBuilder UseSwaggerUi(this IApplicationBuilder app)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "AssetBlock API v1");
            c.DisplayRequestDuration();
        });
        return app;
    }
}
