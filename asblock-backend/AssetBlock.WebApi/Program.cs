using AssetBlock.WebApi.Conventions;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.Services;
using AssetBlock.WebApi.Constants;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Application;
using AssetBlock.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilogConfiguration();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.Configure<RouteOptions>(options => options.LowercaseUrls = true);
builder.Services.AddControllers(options => options.Conventions.Add(new LowercaseControllerRouteConvention()));
builder.Services.AddSignalR();
builder.Services.AddScoped<IRealtimeNotificationPublisher, RealtimeNotificationPublisher>();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerConfiguration();
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddApiRateLimiting();

var app = builder.Build();

app.UseValidationExceptionHandler();
app.UseSerilogRequestLoggingConfiguration();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<AssetBlock.WebApi.Hubs.NotificationsHub>(ApiRoutes.Hubs.NOTIFICATIONS);
app.Run();
