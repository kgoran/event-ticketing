using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SweepIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_seats_held_until_held",
                schema: "inventory",
                table: "seats",
                column: "held_until",
                filter: "status = 'Held'");

            migrationBuilder.CreateIndex(
                name: "ix_holds_expires_at_current",
                schema: "inventory",
                table: "holds",
                column: "expires_at",
                filter: "status IN ('Active', 'InCheckout')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_seats_held_until_held",
                schema: "inventory",
                table: "seats");

            migrationBuilder.DropIndex(
                name: "ix_holds_expires_at_current",
                schema: "inventory",
                table: "holds");
        }
    }
}
