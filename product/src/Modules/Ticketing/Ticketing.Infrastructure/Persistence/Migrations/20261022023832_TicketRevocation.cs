using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Ticketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketRevocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "revocation_reason",
                schema: "ticketing",
                table: "tickets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "revoked_at",
                schema: "ticketing",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revocation_reason",
                schema: "ticketing",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                schema: "ticketing",
                table: "tickets");
        }
    }
}
