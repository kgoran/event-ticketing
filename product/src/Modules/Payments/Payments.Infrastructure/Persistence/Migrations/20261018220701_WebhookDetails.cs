using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WebhookDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "ix_received_webhooks_provider_provider_event_id",
                schema: "payments",
                table: "received_webhooks",
                newName: "ux_received_webhooks_provider_event");

            migrationBuilder.AddColumn<long>(
                name: "amount_minor_units",
                schema: "payments",
                table: "received_webhooks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "currency",
                schema: "payments",
                table: "received_webhooks",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "reference",
                schema: "payments",
                table: "received_webhooks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "payments",
                table: "payment_attempts",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "amount_minor_units",
                schema: "payments",
                table: "received_webhooks");

            migrationBuilder.DropColumn(
                name: "currency",
                schema: "payments",
                table: "received_webhooks");

            migrationBuilder.DropColumn(
                name: "reference",
                schema: "payments",
                table: "received_webhooks");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "payments",
                table: "payment_attempts");

            migrationBuilder.RenameIndex(
                name: "ux_received_webhooks_provider_event",
                schema: "payments",
                table: "received_webhooks",
                newName: "ix_received_webhooks_provider_provider_event_id");
        }
    }
}
