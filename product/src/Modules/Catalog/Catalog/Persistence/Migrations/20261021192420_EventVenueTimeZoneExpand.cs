using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Catalog.Persistence.Migrations
{
    /// <summary>
    /// DP3's worked example, release 1 of 2 (expand): <c>events.time_zone</c> becomes
    /// <c>venue_time_zone</c>. EF Core scaffolds a rename, which breaks the instances of the previous
    /// release that keep serving while this one rolls out (the Migrator runs first): measured in the
    /// lab, 26% of requests failed. So both columns live side by side until release 2 (contract),
    /// and a trigger fills in whichever one a write left out. docs/database-changes.md
    /// </summary>
    public partial class EventVenueTimeZoneExpand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Adding a column without a default changes only the table's definition: instant.
            migrationBuilder.AddColumn<string>(
                name: "venue_time_zone",
                schema: "catalog",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // time_zone got a default of '' when it was added (EF Core does that for a new required
            // column). This release doesn't write it, and the default would fill it in before the
            // trigger could: found by checking the trigger in the lab. The previous release always
            // writes it, so it never needed the default.
            migrationBuilder.Sql("ALTER TABLE catalog.events ALTER COLUMN time_zone DROP DEFAULT;");

            // From now on, a write from either release fills in the other column. Before the copy
            // below, so that no row written in between is missed.
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
                """);

            // The rows written before. One statement is fine for the catalog's few thousand events;
            // a table of millions would be copied in batches, outside the migration's transaction.
            migrationBuilder.Sql("UPDATE catalog.events SET venue_time_zone = time_zone WHERE venue_time_zone IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "venue_time_zone",
                schema: "catalog",
                table: "events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER sync_venue_time_zone ON catalog.events;
                DROP FUNCTION catalog.events_sync_venue_time_zone();
                """);

            migrationBuilder.DropColumn(
                name: "venue_time_zone",
                schema: "catalog",
                table: "events");
        }
    }
}
