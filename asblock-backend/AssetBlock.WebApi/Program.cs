using AssetBlock.WebApi.Extensions;
using AssetBlock.Application;
using AssetBlock.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilogConfiguration();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddOpenApi();
builder.Services.AddSwaggerConfiguration();
builder.Services.AddJwtAuthentication(builder.Configuration);

var app = builder.Build();

app.UseValidationExceptionHandler();
app.UseSerilogRequestLoggingConfiguration();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<AssetBlock.WebApi.Hubs.NotificationsHub>("/hubs/notifications");
app.Run();
