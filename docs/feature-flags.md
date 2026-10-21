# Feature flags

Flags switch behaviour in a running system without a deploy ([ADR-0036](adr/0036-trunk-based-development.md):
unfinished work ships dark behind a flag instead of waiting on a long branch). M2 brings the
mechanism and one flag ([M2 plan](milestones/m2.md), FF1, D45).

## How they work

- **Microsoft.FeatureManagement** reads the `FeatureManagement` section of the configuration,
  in every host (ServiceDefaults' `AddFeatureFlags`). Code asks `IFeatureManager`, or puts an
  endpoint behind a flag with `PausedBy` (Shared.Infrastructure's `FeatureFlags`). Flag names
  are constants in `FeatureFlags`, each with what it does.
- **Under Aspire**, the flags are in each host's `appsettings.json`; saving the file switches
  them (the host re-reads it).
- **In the lab**, they are in the ConfigMap `eventticketing-features`, mounted as a file into
  every instance of every host, which re-reads it while running (`FeatureFlags:File`; .NET polls
  the file, because Kubernetes swaps a ConfigMap's files by a symbolic link that file watchers
  miss). Switching:

  ```
  dotnet run lab.cs -- flag                    # each flag, on or off
  dotnet run lab.cs -- flag PauseHolds on      # switch one
  ```

  Kubernetes updates the file in each instance within about a minute, each on its own: measured,
  every instance had a flag 35–60 s after `flag … on`, and 10–23 s after `off`. For that minute,
  some instances have it and some don't, so a flag must be safe to be half on.
- **A deploy keeps the flags that run.** After the first install they belong to whoever operates
  the system. The chart reads the running ConfigMap and adds only flags it doesn't know yet, from
  `features` in its values; `reset` starts from those values again.

## The flags

| Flag | Kind | What it does |
|---|---|---|
| `PauseHolds` | operator's brake | New holds are answered **503 with Retry-After: 30**, "New holds are paused for a moment. Please try again shortly." Browsing, the holds fans have already and checkouts go on. For an incident: the database struggling at an on-sale, say. The Scaling Lab counts these answers as refused, not failed |

Measured in the lab at 50 requests/s with half of them holds, `PauseHolds` on: 253 holds refused
and 247 browsing answers in 10 s, nothing failed; off again, everything as before.

## Adding a flag

1. A constant in `FeatureFlags`, with what it does and what kind it is: a release toggle (unfinished
   work, removed once it is on everywhere), or an operator's switch (kept).
2. Its default, off, in `features` (product chart values) and in `appsettings.json` of the hosts
   that read it.
3. Code that is correct both with the flag on and off, and while instances disagree for a minute.
4. A test with the flag on (`TestSystem.PublicApiWith`) and off.
5. A release toggle gets a removal date in its comment; a flag left on forever is dead code.
