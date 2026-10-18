using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace EventTicketing.Catalog.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Browse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "seat_map_version",
                schema: "catalog",
                table: "events",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                schema: "catalog",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "venue_name",
                schema: "catalog",
                table: "events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "catalog",
                table: "events",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple', name || ' ' || venue_name)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_events_search_vector",
                schema: "catalog",
                table: "events",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_events_status_starts_at",
                schema: "catalog",
                table: "events",
                columns: new[] { "status", "starts_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_search_vector",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_status_starts_at",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "seat_map_version",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "time_zone",
                schema: "catalog",
                table: "events");

            migrationBuilder.DropColumn(
                name: "venue_name",
                schema: "catalog",
                table: "events");
        }
    }
}
