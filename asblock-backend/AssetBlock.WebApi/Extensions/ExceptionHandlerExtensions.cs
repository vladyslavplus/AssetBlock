using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.ProblemDetails;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
                    var errors = validationException.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                    var problem = AssetBlockProblemDetails.CreateValidation(context, errors);
                    await AssetBlockProblemDetails.Write(context, problem);
                    return;
                }

                var logger = context.RequestServices.GetRequiredService<ILogger<ExceptionHandlerLog>>();
                logger.LogError(
                    exception,
                    "Unhandled exception; traceId={TraceId}",
                    context.TraceIdentifier);

                var internalProblem = AssetBlockProblemDetails.Create(
                    context,
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.ERR_INTERNAL);
                await AssetBlockProblemDetails.Write(context, internalProblem);
            });
        });
        return app;
    }
}

internal sealed class ExceptionHandlerLog;
