using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Organizers.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "organizers");

            migrationBuilder.CreateTable(
                name: "audit_records",
                schema: "organizers",
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
                name: "idempotency_records",
                schema: "organizers",
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
                schema: "organizers",
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
                name: "organizers",
                schema: "organizers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "organizers",
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
                schema: "organizers",
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
                name: "members",
                schema: "organizers",
                columns: table => new
                {
                    organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_members", x => new { x.organizer_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_members_organizers_organizer_id",
                        column: x => x.organizer_id,
                        principalSchema: "organizers",
                        principalTable: "organizers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_members_user_id",
                schema: "organizers",
                table: "members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "organizers",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "processed_at IS NULL AND dead_lettered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_messages_due",
                schema: "organizers",
                table: "scheduled_messages",
                column: "due_at",
                filter: "dead_lettered_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records",
                schema: "organizers");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "organizers");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "organizers");

            migrationBuilder.DropTable(
                name: "members",
                schema: "organizers");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "organizers");

            migrationBuilder.DropTable(
                name: "scheduled_messages",
                schema: "organizers");

            migrationBuilder.DropTable(
                name: "organizers",
                schema: "organizers");
        }
    }
}
