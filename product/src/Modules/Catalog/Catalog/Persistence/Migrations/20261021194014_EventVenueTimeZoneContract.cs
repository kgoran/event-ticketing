using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Catalog.Persistence.Migrations
{
    /// <summary>
    /// DP3's worked example, release 2 of 2 (contract): <c>events.time_zone</c> goes. Release 1
    /// (<see cref="EventVenueTimeZoneExpand"/>) reads and writes only <c>venue_time_zone</c>, so its
    /// instances keep serving while this release rolls out. From here on, the release before the
    /// expand can't be rolled back to. docs/database-changes.md
    /// </summary>
    public partial class EventVenueTimeZoneContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER sync_venue_time_zone ON catalog.events;
                DROP FUNCTION catalog.events_sync_venue_time_zone();
                """);

            migrationBuilder.DropColumn(
                name: "time_zone",
                schema: "catalog",
                table: "events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to release 1's schema: the old column, copied and kept in step again.
            migrationBuilder.AddColumn<string>(
                name: "time_zone",
                schema: "catalog",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION catalog.events_sync_venue_time_zone() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        NEW.venue_time_zone := COALESCE(NEW.venue_time_zone, NEW.time_zone);
                        NEW.time_zone := COALESCE(NEW.time_zone, NEW.venue_time_zone);
                    ELSIF NEW.venue_time_zone IS DISTINCT FROM OLD.venue_time_zone THEN
                        NEW.time_zone := NEW.venue_time_zone;
                    ELSIF NEW.time_zone IS DISTINCT FROM OLD.time_zone THEN
                        NEW.venue_time_zone := NEW.time_zone;
                    END IF;
                    RETURN NEW;
                END $$;

                CREATE TRIGGER sync_venue_time_zone BEFORE INSERT OR UPDATE ON catalog.events
                    FOR EACH ROW EXECUTE FUNCTION catalog.events_sync_venue_time_zone();

                UPDATE catalog.events SET time_zone = venue_time_zone;
                ALTER TABLE catalog.events ALTER COLUMN time_zone SET NOT NULL;
                """);
        }
    }
}
