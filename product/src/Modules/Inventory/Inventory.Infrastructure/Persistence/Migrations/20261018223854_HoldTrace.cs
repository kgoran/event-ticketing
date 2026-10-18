using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HoldTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trace_parent",
                schema: "inventory",
                table: "holds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trace_parent",
                schema: "inventory",
                table: "holds");
        }
    }
}
