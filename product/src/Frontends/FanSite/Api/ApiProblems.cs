using EventTicketing.FanSite.PublicApi.Models;
using HttpValidationProblemDetails = EventTicketing.FanSite.PublicApi.Models.HttpValidationProblemDetails;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace EventTicketing.FanSite.Api;

/// <summary>
/// What to tell the fan when PublicApi says no. Its problem responses carry a title written for
/// people (e.g. "One or more of these places are no longer available."), so the site shows it.
/// </summary>
internal static class ApiProblems
{
    public const string SignInAgain = "Your session has ended. Sign in again to continue.";
    public const string Unavailable = "Something went wrong. Please try again.";

    /// <summary>Failures a page shows as a message instead of the error page.</summary>
    public static bool IsExpected(Exception exception) => exception is ApiException or HttpRequestException;

    public static string MessageFor(Exception exception) => exception switch
    {
        HttpValidationProblemDetails validation => FirstError(validation) ?? validation.Title ?? Unavailable,
        ProblemDetails problem => problem.Title ?? Unavailable,
        ApiException { ResponseStatusCode: 401 } => SignInAgain,
        ApiException { ResponseStatusCode: 404 } => "That doesn't exist (any more).",
        _ => Unavailable,
    };

    private static string? FirstError(HttpValidationProblemDetails validation) =>
        validation.Errors?.AdditionalData.Values
            .OfType<UntypedArray>()
            .SelectMany(messages => messages.GetValue())
            .OfType<UntypedString>()
            .Select(message => message.GetValue())
            .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message));
}
