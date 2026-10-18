using Microsoft.EntityFrameworkCore;

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
    /// <summary>The module's PostgreSQL schema, e.g. "orders".</summary>
    protected abstract string Schema { get; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        InfrastructureModel.Configure(modelBuilder);
    }
}
