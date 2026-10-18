using EventTicketing.Payments.Application;
using EventTicketing.Payments.Application.Webhooks;
using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Payments.Infrastructure.Persistence;

internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "payments";

    /// <summary>Each provider event is stored once: a duplicate arriving at the same moment fails here.</summary>
    public const string OneWebhookPerEvent = "ux_received_webhooks_provider_event";

    private static readonly IReadOnlyDictionary<string, Error> Constraints = new Dictionary<string, Error>
    {
        [OneWebhookPerEvent] = WebhookErrors.Duplicate,
    };

    public DbSet<PaymentAttempt> Attempts => Set<PaymentAttempt>();

    public DbSet<ReceivedWebhook> Webhooks => Set<ReceivedWebhook>();

    public DbSet<Refund> Refunds => Set<Refund>();

    protected override string Schema => SchemaName;

    protected override IReadOnlyDictionary<string, Error> ConstraintErrors => Constraints;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PaymentAttempt>(entity =>
        {
            entity.ToTable("payment_attempts");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Currency).HasMaxLength(3);
            entity.Property(a => a.Provider).HasMaxLength(50);
            entity.Property(a => a.ProviderPaymentId).HasMaxLength(200);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(a => a.OrderId).IsUnique();
            entity.HasIndex(a => new { a.Provider, a.ProviderPaymentId }).IsUnique().HasFilter("provider_payment_id IS NOT NULL");

            // Webhooks, status checks and the checkout coordinator can change an attempt at the same
            // moment: PostgreSQL's xmin makes the later save fail instead of overwriting (ADR-0011).
            entity.Property<uint>("Version").IsRowVersion();
        });

        modelBuilder.Entity<ReceivedWebhook>(entity =>
        {
            entity.ToTable("received_webhooks");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Provider).HasMaxLength(50);
            entity.Property(w => w.ProviderEventId).HasMaxLength(200);
            entity.Property(w => w.Type).HasMaxLength(100);
            entity.Property(w => w.ProviderPaymentId).HasMaxLength(200);
            entity.Property(w => w.Reference).HasMaxLength(200);
            entity.Property(w => w.ProviderRefundId).HasMaxLength(200);
            entity.Property(w => w.Currency).HasMaxLength(3);
            entity.HasIndex(w => new { w.Provider, w.ProviderEventId }).IsUnique().HasDatabaseName(OneWebhookPerEvent);
        });

        modelBuilder.Entity<Refund>(entity =>
        {
            entity.ToTable("refunds");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Currency).HasMaxLength(3);
            entity.Property(r => r.Reason).HasMaxLength(200);
            entity.Property(r => r.FailureReason).HasMaxLength(500);
            entity.Property(r => r.ProviderRefundId).HasMaxLength(200);
            entity.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasIndex(r => r.PaymentAttemptId).IsUnique(); // M1a: a single refund per payment
            entity.HasIndex(r => r.ProviderRefundId);
            entity.Property<uint>("Version").IsRowVersion();
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c>.</summary>
internal sealed class PaymentsDesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) =>
        new(ModuleDesignTime.Options<PaymentsDbContext>(PaymentsDbContext.SchemaName));
}

internal sealed class PaymentAttemptRepository(PaymentsDbContext db) : IPaymentAttemptRepository
{
    public void Add(PaymentAttempt attempt) => db.Attempts.Add(attempt);

    public Task<PaymentAttempt?> FindAsync(Guid attemptId, CancellationToken cancellationToken) =>
        db.Attempts.SingleOrDefaultAsync(a => a.Id == attemptId, cancellationToken);

    public Task<PaymentAttempt?> FindByOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Attempts.SingleOrDefaultAsync(a => a.OrderId == orderId, cancellationToken);

    public Task<PaymentAttempt?> FindByProviderPaymentAsync(string provider, string providerPaymentId, CancellationToken cancellationToken) =>
        db.Attempts.SingleOrDefaultAsync(a => a.Provider == provider && a.ProviderPaymentId == providerPaymentId, cancellationToken);
}

internal sealed class RefundRepository(PaymentsDbContext db) : IRefundRepository
{
    public void Add(Refund refund) => db.Refunds.Add(refund);

    public Task<Refund?> FindAsync(Guid refundId, CancellationToken cancellationToken) =>
        db.Refunds.SingleOrDefaultAsync(r => r.Id == refundId, cancellationToken);

    public Task<Refund?> FindByAttemptAsync(Guid attemptId, CancellationToken cancellationToken) =>
        db.Refunds.SingleOrDefaultAsync(r => r.PaymentAttemptId == attemptId, cancellationToken);

    public Task<Refund?> FindByProviderRefundAsync(string providerRefundId, CancellationToken cancellationToken) =>
        db.Refunds.SingleOrDefaultAsync(r => r.ProviderRefundId == providerRefundId, cancellationToken);
}

internal sealed class WebhookRepository(PaymentsDbContext db) : IWebhookRepository
{
    public void Add(ReceivedWebhook webhook) => db.Webhooks.Add(webhook);

    public Task<bool> ExistsAsync(string provider, string providerEventId, CancellationToken cancellationToken) =>
        db.Webhooks.AnyAsync(w => w.Provider == provider && w.ProviderEventId == providerEventId, cancellationToken);

    public Task<ReceivedWebhook?> FindAsync(Guid webhookId, CancellationToken cancellationToken) =>
        db.Webhooks.SingleOrDefaultAsync(w => w.Id == webhookId, cancellationToken);
}
