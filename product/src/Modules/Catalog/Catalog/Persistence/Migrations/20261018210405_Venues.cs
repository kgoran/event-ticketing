using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Venues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seats",
                schema: "catalog");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "on_sale_at",
                schema: "catalog",
                table: "events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "venue_id",
                schema: "catalog",
                table: "events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "price_assignments",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    row = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    price_category_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_assignments_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organizer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_venues", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "venue_sections",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: true),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_venue_sections", x => x.id);
                    table.ForeignKey(
                        name: "fk_venue_sections_venues_venue_id",
                        column: x => x.venue_id,
                        principalSchema: "catalog",
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "venue_seats",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    x = table.Column<int>(type: "integer", nullable: false),
                    y = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_venue_seats", x => x.id);
                    table.ForeignKey(
                        name: "fk_venue_seats_venue_sections_section_id",
                        column: x => x.section_id,
                        principalSchema: "catalog",
                        principalTable: "venue_sections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_venue_id",
                schema: "catalog",
                table: "events",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_assignments_event_id",
                schema: "catalog",
                table: "price_assignments",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_venue_seats_section_id_row_number",
                schema: "catalog",
                table: "venue_seats",
                columns: new[] { "section_id", "row", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_venue_sections_venue_id_code",
                schema: "catalog",
                table: "venue_sections",
                columns: new[] { "venue_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_venues_organizer_id",
                schema: "catalog",
                table: "venues",
                column: "organizer_id");

            migrationBuilder.AddForeignKey(
                name: "fk_events_venues_venue_id",
                schema: "catalog",
                table: "events",
                column: "venue_id",
                principalSchema: "catalog",
                principalTable: "venues",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_events_venues_venue_id",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropTable(
                name: "price_assignments",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venue_seats",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venue_sections",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "ix_events_venue_id",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "on_sale_at",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "venue_id",
                schema: "catalog",
                table: "events");

            migrationBuilder.CreateTable(
                name: "seats",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    price_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    section = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
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
                name: "ix_seats_event_id",
                schema: "catalog",
                table: "seats",
                column: "event_id");
        }
    }
}
