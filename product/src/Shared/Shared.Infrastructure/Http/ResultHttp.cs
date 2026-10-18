using EventTicketing.Shared.Domain;
using Microsoft.AspNetCore.Http;

namespace EventTicketing.Shared.Infrastructure.Http;

/// <summary>Turns expected business failures into RFC 9457 problem responses with a stable error code.</summary>
public static class ResultHttp
{
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        return error is ValidationError validation
            ? TypedResults.ValidationProblem(
                validation.Errors
                    .GroupBy(failure => failure.Field)
                    .ToDictionary(group => group.Key, group => group.Select(failure => failure.Message).ToArray()),
                title: validation.Message,
                extensions: extensions)
            : TypedResults.Problem(title: error.Message, statusCode: StatusCodeOf(error.Type), extensions: extensions);
    }

    private static int StatusCodeOf(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status422UnprocessableEntity,
    };
}
