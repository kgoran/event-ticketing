using EventTicketing.Shared.Domain;

namespace EventTicketing.Shared.UnitTests.Domain;

public sealed class ResultTests
{
    [Fact]
    public void A_success_carries_its_value()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
        result.Error.Should().Be(Error.None);
    }

    [Fact]
    public void A_failure_carries_its_error_and_has_no_value()
    {
        Result<int> result = Error.Conflict("seat.taken", "The seat is taken.");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("seat.taken");
        result.Error.Type.Should().Be(ErrorType.Conflict);

        var readValue = () => result.Value;
        readValue.Should().Throw<InvalidOperationException>().WithMessage("*seat.taken*");
    }

    [Fact]
    public void A_failure_needs_a_real_error()
    {
        var create = () => Result.Failure(Error.None);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_validation_error_lists_every_invalid_field()
    {
        var error = new ValidationError([new FieldError("Name", "Required."), new FieldError("Seats", "Too many.")]);

        error.Type.Should().Be(ErrorType.Validation);
        error.Errors.Should().HaveCount(2);
    }
}
