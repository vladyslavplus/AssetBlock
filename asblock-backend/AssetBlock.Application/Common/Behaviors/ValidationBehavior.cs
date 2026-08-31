using AssetBlock.Application.Messaging;
using FluentValidation;
using FluentValidation.Results;

namespace AssetBlock.Application.Common.Behaviors;

internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        IValidator<TRequest>[] validatorArray = validators as IValidator<TRequest>[] ?? validators.ToArray();
        if (validatorArray.Length == 0)
        {
            return await next(cancellationToken);
        }

        var context = new ValidationContext<TRequest>(request);
        ValidationResult[] results = await Task.WhenAll(validatorArray.Select(v => v.ValidateAsync(context, cancellationToken)));
        var failures = results.SelectMany(r => r.Errors).Where(f => f != null).ToList();

        if (failures.Count == 0)
        {
            return await next(cancellationToken);
        }

        throw new ValidationException(failures);
    }
}
