using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CheckoutTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "checkout_trace_parent",
                schema: "payments",
                table: "payment_attempts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "checkout_trace_parent",
                schema: "payments",
                table: "payment_attempts");
        }
    }
}
