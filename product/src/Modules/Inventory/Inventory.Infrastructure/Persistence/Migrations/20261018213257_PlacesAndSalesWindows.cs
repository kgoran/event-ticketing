using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlacesAndSalesWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_holds_event_id_customer_id",
                schema: "inventory",
                table: "holds");

            migrationBuilder.AlterColumn<string>(
                name: "row",
                schema: "inventory",
                table: "seats",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<int>(
                name: "number",
                schema: "inventory",
                table: "seats",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "rank",
                schema: "inventory",
                table: "seats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "sales_windows",
                schema: "inventory",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_sale_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sales_end_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    on_sale_set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sales_windows", x => x.event_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_seats_event_id_section_rank",
                schema: "inventory",
                table: "seats",
                columns: new[] { "event_id", "section", "rank" });

            migrationBuilder.CreateIndex(
                name: "ux_holds_one_current_per_fan",
                schema: "inventory",
                table: "holds",
                columns: new[] { "event_id", "customer_id" },
                unique: true,
                filter: "status IN ('Active', 'InCheckout')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sales_windows",
                schema: "inventory");

            migrationBuilder.DropIndex(
                name: "ix_seats_event_id_section_rank",
                schema: "inventory",
                table: "seats");

            migrationBuilder.DropIndex(
                name: "ux_holds_one_current_per_fan",
                schema: "inventory",
                table: "holds");

            migrationBuilder.DropColumn(
                name: "rank",
                schema: "inventory",
                table: "seats");

            migrationBuilder.AlterColumn<string>(
                name: "row",
                schema: "inventory",
                table: "seats",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "number",
                schema: "inventory",
                table: "seats",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_holds_event_id_customer_id",
                schema: "inventory",
                table: "holds",
                columns: new[] { "event_id", "customer_id" });
        }
    }
}
