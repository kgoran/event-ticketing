using EventTicketing.Shared.Application.Commands;
using EventTicketing.Shared.Domain;
using FluentValidation;

namespace EventTicketing.Shared.Application.Pipeline;

/// <summary>Step 3: runs the command's FluentValidation validators and stops on any failure.</summary>
internal sealed class ValidationDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var failures = new List<FieldError>();

        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(command, cancellationToken);
            failures.AddRange(result.Errors.Select(e => new FieldError(e.PropertyName, e.ErrorMessage)));
        }

        if (failures.Count > 0)
        {
            return new ValidationError(failures);
        }

        return await inner.HandleAsync(command, cancellationToken);
    }
}
