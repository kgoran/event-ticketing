using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Orders.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefundReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "refund_reason",
                schema: "orders",
                table: "orders",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "refund_reason",
                schema: "orders",
                table: "orders");
        }
    }
}
