using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Extensions;

internal static class ExceptionHandlerExtensions
{
    public static IApplicationBuilder UseValidationExceptionHandler(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(handler =>
        {
            handler.Run(async context =>
            {
                var exceptionHandlerFeature = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionHandlerFeature?.Error;

                if (exception is ValidationException validationException)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.ContentType = "application/problem+json";
                    var errors = validationException.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                    var problemDetails = new ValidationProblemDetails(errors)
                    {
                        Title = "Validation failed",
                        Status = (int)HttpStatusCode.BadRequest,
                        Detail = "One or more validation errors occurred."
                    };
                    await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails));
                    return;
                }

                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new ProblemDetails
                {
                    Title = "An error occurred",
                    Status = (int)HttpStatusCode.InternalServerError
                }));
            });
        });
        return app;
    }
}
