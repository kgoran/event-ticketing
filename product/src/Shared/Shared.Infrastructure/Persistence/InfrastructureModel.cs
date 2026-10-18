using Microsoft.EntityFrameworkCore;

namespace EventTicketing.Shared.Infrastructure.Persistence;

// The infrastructure tables in every module's schema. Column names are explicit because the
// outbox, inbox and scheduler query them with hand-written SQL (claiming rows with SKIP LOCKED).

internal sealed class OutboxMessage
{
    public Guid Id { get; init; }

    public required string Type { get; init; }

    public int Version { get; init; }

    public required string Payload { get; init; }

    public required string Headers { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public int Attempts { get; init; }

    public DateTimeOffset NextAttemptAt { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public DateTimeOffset? ProcessedAt { get; init; }

    public DateTimeOffset? DeadLetteredAt { get; init; }

    public string? LastError { get; init; }
}

internal sealed class InboxMessage
{
    public Guid MessageId { get; init; }

    public required string Consumer { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}

internal sealed class ScheduledMessage
{
    public Guid Id { get; init; }

    public required string Type { get; init; }

    public required string Payload { get; init; }

    public required string Headers { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public int Attempts { get; init; }

    public DateTimeOffset? LockedUntil { get; init; }

    public DateTimeOffset? DeadLetteredAt { get; init; }

    public string? LastError { get; init; }
}

internal sealed class IdempotencyRecord
{
    public required string Key { get; init; }

    public required string Request { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset LockedUntil { get; init; }

    public string? Result { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }
}

internal sealed class AuditRecord
{
    public Guid Id { get; init; }

    public required string Command { get; init; }

    public string? UserId { get; init; }

    public string? Reason { get; init; }

    public DateTimeOffset At { get; init; }
}

internal static class InfrastructureModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.Type).HasColumnName("type");
            entity.Property(m => m.Version).HasColumnName("version");
            entity.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(m => m.Headers).HasColumnName("headers").HasColumnType("jsonb");
            entity.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            entity.Property(m => m.Attempts).HasColumnName("attempts");
            entity.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at").HasDefaultValueSql("now()");
            entity.Property(m => m.LockedUntil).HasColumnName("locked_until");
            entity.Property(m => m.ProcessedAt).HasColumnName("processed_at");
            entity.Property(m => m.DeadLetteredAt).HasColumnName("dead_lettered_at");
            entity.Property(m => m.LastError).HasColumnName("last_error");
            entity.HasIndex(m => m.NextAttemptAt)
                .HasDatabaseName("ix_outbox_messages_pending")
                .HasFilter("processed_at IS NULL AND dead_lettered_at IS NULL");
        });

        model.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(m => new { m.MessageId, m.Consumer });
            entity.Property(m => m.MessageId).HasColumnName("message_id");
            entity.Property(m => m.Consumer).HasColumnName("consumer");
            entity.Property(m => m.ProcessedAt).HasColumnName("processed_at").HasDefaultValueSql("now()");
        });

        model.Entity<ScheduledMessage>(entity =>
        {
            entity.ToTable("scheduled_messages");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(m => m.Type).HasColumnName("type");
            entity.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(m => m.Headers).HasColumnName("headers").HasColumnType("jsonb");
            entity.Property(m => m.DueAt).HasColumnName("due_at");
            entity.Property(m => m.Attempts).HasColumnName("attempts");
            entity.Property(m => m.LockedUntil).HasColumnName("locked_until");
            entity.Property(m => m.DeadLetteredAt).HasColumnName("dead_lettered_at");
            entity.Property(m => m.LastError).HasColumnName("last_error");
            entity.HasIndex(m => m.DueAt)
                .HasDatabaseName("ix_scheduled_messages_due")
                .HasFilter("dead_lettered_at IS NULL");
        });

        model.Entity<IdempotencyRecord>(entity =>
        {
            entity.ToTable("idempotency_records");
            entity.HasKey(r => new { r.Key, r.Request });
            entity.Property(r => r.Key).HasColumnName("key");
            entity.Property(r => r.Request).HasColumnName("request");
            entity.Property(r => r.Status).HasColumnName("status");
            entity.Property(r => r.LockedUntil).HasColumnName("locked_until");
            entity.Property(r => r.Result).HasColumnName("result").HasColumnType("jsonb");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
        });

        model.Entity<AuditRecord>(entity =>
        {
            entity.ToTable("audit_records");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Command).HasColumnName("command");
            entity.Property(r => r.UserId).HasColumnName("user_id");
            entity.Property(r => r.Reason).HasColumnName("reason");
            entity.Property(r => r.At).HasColumnName("at");
        });
    }
}
