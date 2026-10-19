using EventTicketing.FanSite.Api;
using EventTicketing.FanSite.PublicApi.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace EventTicketing.FanSite.Tests;

public sealed class ApiProblemsTests
{
    [Fact]
    public void A_refused_request_shows_the_title_publicapi_wrote_for_people() =>
        ApiProblems.MessageFor(new ProblemDetails { Title = "One or more of these places are no longer available.", Status = 409 })
            .Should().Be("One or more of these places are no longer available.");

    [Fact]
    public void A_validation_problem_shows_its_first_message()
    {
        var problem = new HttpValidationProblemDetails
        {
            Title = "One or more validation errors occurred.",
            Errors = new HttpValidationProblemDetails_errors
            {
                AdditionalData = { ["SeatIds"] = new UntypedArray([new UntypedString("Hold at most 8 seats at a time.")]) },
            },
        };

        ApiProblems.MessageFor(problem).Should().Be("Hold at most 8 seats at a time.");
    }

    [Fact]
    public void An_expired_session_asks_the_fan_to_sign_in_again() =>
        ApiProblems.MessageFor(new ApiException("Unauthorized") { ResponseStatusCode = 401 }).Should().Be(ApiProblems.SignInAgain);

    [Fact]
    public void Anything_else_is_a_generic_message_and_never_the_exception_text() =>
        ApiProblems.MessageFor(new HttpRequestException("Connection refused (publicapi:80)")).Should().Be(ApiProblems.Unavailable);
}
