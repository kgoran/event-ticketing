using EventTicketing.Shared.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventTicketing.Shared.Infrastructure.Persistence;

/// <summary>
/// Base class for every module's DbContext. It puts the module's tables in the module's own
/// schema (ADR-0002) and adds the infrastructure tables every module has: outbox, inbox,
/// scheduled messages, idempotency records and audit records (ADR-0014, ADR-0033).
/// </summary>
/// <remarks>
/// Overrides of <see cref="OnModelCreating"/> must call the base method first. Handlers never call
/// <c>SaveChanges</c>: the transaction runner saves once, after the handler succeeds.
/// </remarks>
public abstract class ModuleDbContext(DbContextOptions options) : DbContext(options)
{
    private static readonly IReadOnlyDictionary<string, Error> NoConstraintErrors = new Dictionary<string, Error>();

    /// <summary>The module's PostgreSQL schema, e.g. "orders".</summary>
    protected abstract string Schema { get; }

    /// <summary>
    /// Unique constraints whose violation is an expected business outcome, not a bug, by constraint
    /// name. When saving violates one, the transaction runner rolls back and returns this error, so
    /// a race that a handler's own check can't see still ends as a clean conflict.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, Error> ConstraintErrors => NoConstraintErrors;

    /// <summary>The expected outcome a failed save stands for, or null if it's a real failure.</summary>
    internal Error? ErrorFor(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } name }
        && ConstraintErrors.TryGetValue(name, out var error)
            ? error
            : null;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        InfrastructureModel.Configure(modelBuilder);
    }
}
