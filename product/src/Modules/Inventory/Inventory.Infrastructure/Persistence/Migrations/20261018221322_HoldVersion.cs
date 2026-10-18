using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "inventory",
                table: "holds",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "inventory",
                table: "holds");
        }
    }
}
