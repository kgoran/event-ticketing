# Changing the database without downtime: expand and contract

A release's migrations run before its instances roll out (the Migrator Job, D32). While they
roll out, instances of the previous release keep serving on the database the migrations have
just changed. So **every migration has to work for two releases at once: its own and the one
before it.** This is checked: `MigrationRulesTests` fails on a migration that renames, or drops
anything outside a contract step ([M2 plan](milestones/m2.md), DP3).

## The rule

| Change | Safe in one release? | How |
|---|---|---|
| Add a table, a nullable column, a column with a default, an index | Yes | The previous release doesn't know about it and doesn't mind |
| Add a required column | Yes, with care | Give it a default, or fill it in a trigger, so the previous release's inserts still work |
| Rename a column or table | **No** | Expand, then contract, below |
| Drop a column or table | **Not in the release that stops using it** | Stop using it in release N; drop it in N+1, in a migration named `…Contract` |
| Change a column's type | **No** | A new column with the new type, as for a rename |
| Make a column required | Only once every running release writes it | Backfill first; then the constraint, in a later release |

- **Expand** (release N): add the new shape next to the old one, keep both in step (a trigger,
  or the application writing both), copy the existing rows, and switch the code to the new shape.
- **Contract** (release N+1 or later): once no running release uses the old shape, remove it.
  After a contract, the release before the expand can't be rolled back to; the expand release can.

Keep expand migrations short: they take locks while instances serve. Adding a column without a
default only changes the table's definition. A copy of millions of rows belongs in batches,
outside the migration's transaction.

## The worked example: `events.time_zone` becomes `venue_time_zone`

The column holds the venue's time zone, copied into the event when it is created, so the name
should say whose time zone it is. The C# property and the API stay `TimeZone`: only the storage
changes. Browsing reads the column on every event list and event page, so a mistake shows under
load at once.

### What EF Core does by itself

Change the mapping and `dotnet ef migrations add` scaffolds one statement,
`RenameColumn("time_zone" → "venue_time_zone")`. Deployed with `lab deploy --load 100`, the
Migrator renamed the column before the rollout, and every instance of the previous release
failed its browsing queries until it was replaced: **2,405 of 9,243 requests failed (26%)**.

### Release 1: expand

[`EventVenueTimeZoneExpand`](../product/src/Modules/Catalog/Catalog/Persistence/Migrations/20261021192420_EventVenueTimeZoneExpand.cs),
with the mapping changed to `HasColumnName("venue_time_zone")`:

1. Add `venue_time_zone`, nullable for now.
2. Drop `time_zone`'s default of `''` (EF Core gives a new required column one). This release
   doesn't write `time_zone`, and the default would fill it in before the trigger could. Checking
   the trigger in the lab found this; `ExpandContractTests` now does.
3. A trigger: an insert or update that sets one column sets the other. Created before the copy,
   so that no row written in between is missed.
4. Copy the existing rows, then make `venue_time_zone` required.

Deployed onto the previous release with `lab deploy --load 100`: **0 of 7,586 requests failed.**
Both releases' writes keep the two columns equal (`ExpandContractTests`).

### Release 2: contract

[`EventVenueTimeZoneContract`](../product/src/Modules/Catalog/Catalog/Persistence/Migrations/20261021194014_EventVenueTimeZoneContract.cs),
once release 1 runs everywhere: drop the trigger, its function and `time_zone`. Release 1 never
reads `time_zone`, so its instances keep serving while release 2 rolls out. The code doesn't
change; `ExpandContractTests` goes, with the column it checked.

Deployed onto release 1 with `lab deploy --load 100`: **0 of 7,412 requests failed.**

| Release | Requests during the rollout | Failed |
|---|---|---|
| EF Core's rename, in one release | 9,243 | 2,405 (26%) |
| Release 1, expand | 7,586 | 0 |
| Release 2, contract | 7,412 | 0 |

## Checklist for a pull request with a migration

- Does the previous release still work against this schema? Its reads, and its inserts?
- Does a rename or type change go through expand and contract?
- Does a drop come at least one release after the code stopped using it, in a `…Contract` migration?
- Is it fast enough to run while instances serve? Copies in batches, indexes `CONCURRENTLY`
  (outside a transaction).
- Tried with `dotnet run lab.cs -- deploy --load 100`?
