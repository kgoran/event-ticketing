using EventTicketing.Payments.Application;
using EventTicketing.Payments.Domain;
using EventTicketing.Shared.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventTicketing.Payments.Infrastructure.Persistence;

internal sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : ModuleDbContext(options)
{
    public const string SchemaName = "payments";

    public DbSet<PaymentAttempt> Attempts => Set<PaymentAttempt>();

    public DbSet<ReceivedWebhook> Webhooks => Set<ReceivedWebhook>();

    protected override string Schema => SchemaName;

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
        });

        modelBuilder.Entity<ReceivedWebhook>(entity =>
        {
            entity.ToTable("received_webhooks");
            entity.HasKey(w => w.Id);
            entity.Property(w => w.Provider).HasMaxLength(50);
            entity.Property(w => w.ProviderEventId).HasMaxLength(200);
            entity.Property(w => w.Type).HasMaxLength(100);
            entity.Property(w => w.ProviderPaymentId).HasMaxLength(200);
            entity.HasIndex(w => new { w.Provider, w.ProviderEventId }).IsUnique();
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

internal sealed class WebhookRepository(PaymentsDbContext db) : IWebhookRepository
{
    public void Add(ReceivedWebhook webhook) => db.Webhooks.Add(webhook);

    public Task<bool> ExistsAsync(string provider, string providerEventId, CancellationToken cancellationToken) =>
        db.Webhooks.AnyAsync(w => w.Provider == provider && w.ProviderEventId == providerEventId, cancellationToken);

    public Task<ReceivedWebhook?> FindAsync(Guid webhookId, CancellationToken cancellationToken) =>
        db.Webhooks.SingleOrDefaultAsync(w => w.Id == webhookId, cancellationToken);
}
