using AssetBlock.WebApi.Conventions;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.Services;
using AssetBlock.WebApi.Constants;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Application;
using AssetBlock.Infrastructure;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.WebApi.Outbox;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilogConfiguration();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddFileUploadLimits(builder.Configuration);
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
builder.Services.AddControllers(options => options.Conventions.Add(new LowercaseControllerRouteConvention()))
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(e => e.Value is { Errors.Count: > 0 })
                .ToDictionary(
                    e => e.Key,
                    e => e.Value!.Errors
                        .Select(err => string.IsNullOrWhiteSpace(err.ErrorMessage) ? "Invalid value." : err.ErrorMessage)
                        .ToArray());
            var problem = AssetBlock.WebApi.ProblemDetails.AssetBlockProblemDetails.CreateValidation(
                context.HttpContext,
                errors);
            return AssetBlock.WebApi.ProblemDetails.AssetBlockProblemDetails.ToActionResult(problem);
        };
    });
builder.Services.AddAssetBlockCors(builder.Configuration, builder.Environment);
builder.Services.AddSignalR();
builder.Services.AddScoped<IRealtimeNotificationPublisher, RealtimeNotificationPublisher>();
builder.Services.AddScoped<IOutboxMessageHandler, NotificationDispatchOutboxHandler>();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerConfiguration();
builder.Services.AddJwtAuthentication(builder.Configuration);
if (builder.Environment.IsEnvironment("IntegrationTesting"))
{
    builder.Services.AddIntegrationTestingRateLimiting();
}
else
{
    builder.Services.AddApiRateLimiting();
}

var app = builder.Build();

app.UseValidationExceptionHandler();
app.UseSerilogRequestLoggingConfiguration();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi();
}

if (!app.Environment.IsEnvironment("IntegrationTesting") && !app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAssetBlockCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapHub<AssetBlock.WebApi.Hubs.NotificationsHub>(ApiRoutes.Hubs.NOTIFICATIONS);
app.Run();

public partial class Program;
