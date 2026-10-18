using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "audit_records",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    command = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "catalog",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    request = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.key, x.request });
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "catalog",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    consumer = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.message_id, x.consumer });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_messages",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dead_lettered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_categories",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    price_minor_units = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_categories", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_categories_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "seats",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    row = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    price_category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seats", x => x.id);
                    table.ForeignKey(
                        name: "fk_seats_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_organizer_id",
                schema: "catalog",
                table: "events",
                column: "organizer_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "catalog",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_price_categories_event_id",
                schema: "catalog",
                table: "price_categories",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_messages_due",
                schema: "catalog",
                table: "scheduled_messages",
                column: "due_at",
                filter: "dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_seats_event_id",
                schema: "catalog",
                table: "seats",
                column: "event_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "price_categories",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "scheduled_messages",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "seats",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "events",
                schema: "catalog");
        }
    }
}
