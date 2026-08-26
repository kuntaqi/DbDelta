# DbDelta — database compare & one-way sync

Personal tool. Purpose: compare two databases (schema + data) and sync one
direction, at three granularities — schema only, one table's data, or the whole DB.

## Decisions (locked)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Engine | SQL Server first. Provider-abstracted so PostgreSQL slots in without touching the diff engine. |
| 2 | Interface | Local web UI: ASP.NET Core API (.NET 10) + React 19 SPA in TypeScript, Vite. |
| 3 | Sync execution | Always emit a reviewable `.sql` script. Apply is opt-in, transactional, and hard-blocked on read-only servers. |

Why React over Angular: the only hard UI requirement is virtualized diff grids and tree tables, and
TanStack Table + TanStack Virtual are the best free answer to exactly that — React-first, with a much
younger Angular adapter. Kendo is licence-bound so it is off the table either way, which means Angular
would mean hand-building the grid. This is a single-user local tool, so Angular's structure buys nothing here.

## Non-goals

- Not a migration/versioning tool (that's a schema-migration tool's job). No history, no baselines.
- Not multi-master. Sync is strictly one direction per run.
- No cross-engine sync (SQL Server -> PostgreSQL). Compare is per-engine, source and target
  must share a provider.

## Architecture

```
DbDelta.sln
  src/DbDelta.Core/          engine-neutral: model, diff, script assembly, safety
  src/DbDelta.SqlServer/     provider: sys.* catalog reader, T-SQL emitter
  src/DbDelta.PostgreSql/    provider: later, information_schema/pg_catalog
  src/DbDelta.Api/           ASP.NET Core host: endpoints + OpenAPI, serves the built SPA
  src/DbDelta.Web/           React 19 + TypeScript + Vite SPA
  tests/DbDelta.Core.Tests/  diff engine unit tests (no DB needed)
```

The API is the only thing that touches a database; the SPA holds no credentials and issues no SQL.
TypeScript types are generated from the API's OpenAPI document rather than hand-maintained, so a
changed response shape breaks the build instead of failing silently at runtime.

`Core` never references a provider package. Everything engine-specific sits behind:

- `IDatabaseProvider` — factory + capability flags
- `ISchemaReader` — reads the neutral schema model
- `IRowSetReader` — streams key+hash pairs, and fetches full rows on demand
- `IScriptEmitter` — turns a diff into DDL/DML for that engine, including how that engine handles statements
  that must begin their own batch (T-SQL `CREATE VIEW`/`PROCEDURE`/`SCHEMA` need `EXEC sp_executesql` wrapping
  inside a single-transaction script; PostgreSQL does not)
- `IIdentifierQuoter` — `[x]` vs `"x"`

### Neutral schema model

`Table`, `Column`, `PrimaryKey`, `UniqueConstraint`, `Index`, `ForeignKey`, `CheckConstraint`,
`DefaultConstraint`, `View`, `Routine` (proc/function), `Trigger`, `Sequence`, `UserDefinedType`.
Each carries a `ProviderExtras` bag for engine-only attributes (filegroup, fillfactor, collation…)
so the reader loses nothing even though the comparer ignores what it doesn't understand.

### Data compare — how it scales

Naive "pull both tables and compare in memory" dies on big tables. Two passes instead:

1. **Key+hash pass.** Both sides stream `(key columns, row_hash)` ordered by key. Provider supplies
   the hash expression (SQL Server: `HASHBYTES('SHA2_256', CONCAT_WS('|', …))`). A merge-join over the
   two ordered streams classifies every row as insert / update / delete / same using O(1) memory.
   This gives exact counts fast, without moving row data.
2. **Detail pass, on demand.** Only for rows the UI is actually showing or the script actually needs,
   fetch the full row by key. Cell-level highlighting comes from comparing those fetched rows.

Column-set differences are resolved before pass 1: the compare runs over the **intersection** of
columns, and the UI reports excluded columns explicitly rather than silently ignoring them.

### The sync plan is a cart, with dependency closure

Selections accumulate across screens — tick objects on the overview, tick rows on the data screen, and
both land in one plan. There is no separate "add to plan" step to forget. Three properties make it more
than a passive basket:

- **Closure, not literal selection.** Ticking `dbo.Company` pulls in what it needs: `dbo.Category`
  goes first because of `FK_Company_Category`. The plan can therefore contain more than was clicked,
  and the UI must show what was added on your behalf and why — a silently larger plan is a trap.
- **Tool-decided order.** Steps are topologically sorted by dependency, not by click order — FKs for tables,
  `sys.sql_expression_dependencies` for views and routines, since those depend through their SQL bodies rather
  than through constraints. Tables are created bare and their FKs added afterwards, which avoids FK cycles
  outright. This is what lets the whole plan run in one transaction. See *Empty target* below for the full order.
- **Carts go stale.** You compare at 14:02, pick for ten minutes, meanwhile someone deploys to UAT.
  Before applying, the plan re-verifies that the target still matches what was compared (object
  fingerprints + row hashes for affected keys). A drift aborts the apply and asks for a fresh compare
  rather than running against a database it no longer understands.

Plan state lives in the API session, not only in browser memory, so a refresh doesn't lose the picking work.

### Scope escalation: picking rows, then deciding "just do the whole database"

The cart must never hold SQL fragments. If it did, escalating to whole-database scope would leave the
earlier per-table fragments in place and emit duplicate `INSERT`s. So the cart holds **intents** —
`(scope, object identity)` — and SQL is compiled exactly once, at the end, from the normalized set.

Normalization runs over a scope lattice, `Database ⊃ Table ⊃ Row`. Selecting an ancestor collapses every
descendant selection beneath it: they are dropped as already covered, not merged. Change units are then
deduplicated by identity — `(objectType, schema, name, changeKind)` for schema, `(schema, table, keyTuple,
operation)` for data — so overlapping scopes are structurally incapable of producing the same statement
twice. The guarantee comes from the identity key, not from care at the call site.

**Escalation is not a pure superset, and that is the interesting case.** Positive picks are subsumed
safely. Negative ones are not:

- an unticked `DROP dbo.SegmentLegacy`
- a deliberately skipped delete of `CompanyId 908`, still referenced by 12 rows in `dbo.Site`
- 128 of 130 rows taken, because 2 were wrong

A naive reading of "whole database" pulls all three back in — which silently overrides judgment already
exercised. So exclusions are stored as **first-class negative entries**, not as the absence of a tick, and
they survive escalation. Escalating means "also take everything I haven't considered", never "forget what
I decided". The UI lists surviving exclusions so they can be revoked deliberately.

One consequence worth stating: an exclusion is only meaningful against a known change. If a later compare
surfaces a *different* change to the same object, that is a new change unit with a new identity and it is
not covered by the old exclusion.

### Volume awareness: footprint vs transfer

Two different numbers, and conflating them is what makes size readouts useless:

- **Footprint** — how big the object already is. Context for judgement.
- **Transfer** — how much *this* sync actually moves. The consequence.

A 40 GB table with 3 changed rows is harmless; a 200 MB table seeded in full is heavier. Only transfer
predicts pain, so transfer is the number shown next to the buttons, with footprint as supporting context.

Both come from `sys.dm_db_partition_stats` (`row_count`, `used_page_count * 8 KB`) plus `sys.database_files`
for database totals. These are maintained catalog values, not scans, so the readout is instant even on a
40 GB database and can be fetched during the connection test. Row counts there are approximate; exact
counts are only taken for tables actually entering a data compare.

**The script-size cliff is real and gets its own guard.** `INSERT … VALUES` caps at 1000 rows per statement,
and a `.sql` file past roughly 100 MB stops being openable in SSMS — which defeats the whole "review the
script first" premise. So the plan estimates script bytes up front. Past the threshold the tool says so and
switches to a staged bulk path (`SqlBulkCopy` on apply, `BULK INSERT` + data file for download) instead of
silently emitting a 400 MB file nobody can read.

### Per-table data modes: seeding instead of copying everything

Data scope is chosen per table, not once for the whole run:

| Mode | Meaning | Insert | Update | Delete |
|------|---------|--------|--------|--------|
| `All rows` | full compare | yes | yes | yes, blast-radius guarded |
| `Top N` | first N rows by primary key ascending | yes | yes | **suppressed** |
| `Filter` | rows matching a `WHERE` predicate | yes | yes | **suppressed** |
| `Schema only` | structure, no data | — | — | — |

**Deletes must be suppressed whenever the row set is limited.** Under `All rows`, a row present on the
target but absent from the source means *delete*. Under `Top N` it only means *outside the top 100* — so
honouring deletes would wipe the target while "seeding" it. The limited scope is not authoritative about
absence, and the tool must not pretend otherwise. This is not a preference; it is a correctness rule.

`Top N` orders by primary key ascending so the same N rows come back on every run — an unordered `TOP` would
make seeding irreproducible.

**Seeding breaks referential integrity unless parents come along.** Seed the top 100 `dbo.Company` and any
`dbo.Site` rows referencing companies outside that 100 either orphan or fail the FK. So seeded tables get
**parent closure**: FKs are followed upward and the required parent rows are pulled in, reusing the same
dependency machinery the cart already has. Closure runs upward only — pulling children transitively is the
full database-subsetting problem and would quietly drag in most of the database. When a seed's children
cannot be satisfied, the tool reports which FKs are affected rather than guessing.

### Empty target: the case that stresses everything else

An empty target is the cleanest possible schema diff — every source object is source-only, so every change
is a create. But three things change materially, and two of them exposed gaps in the design above.

**1. The default flips.** Target footprint is zero, so transfer equals the *entire source*. Seeding an empty
database from a 41.6 GB source is a 41.6 GB transfer and a script measured in tens of gigabytes — the
script-size cliff stops being an edge case and becomes the default outcome. So an empty target defaults every
table to `Schema only`, and data is opted into per table. `Top N` on lookup tables is the expected shape here:
structure plus seed, not a clone. Deletes are inert (nothing to delete) and the drift check trivially passes.

**2. Ordering stops being an FK problem.** With incremental diffs, FK ordering covers a handful of tables.
Creating a whole database needs the full dependency order, and views/procedures/functions depend on each
other through their SQL bodies, not through foreign keys — a topological sort over FKs alone will emit a view
before the view it selects from. Three mechanisms, in order of preference:

- Split tables from their FKs: create all tables bare, then add every FK afterwards. This sidesteps FK cycles
  entirely rather than needing `NOCHECK` bracketing, which matters because cycles are far more likely across a
  whole database than across a few picked tables.
- Order programmable objects with `sys.sql_expression_dependencies` rather than parsing SQL bodies. Done:
  the reader captures `DependsOn` per view/routine and the emitter topologically sorts them. Worth knowing
  why this needed a real test — alphabetical order happened to match dependency order in the first schema
  it ran against, so the gap stayed invisible until a dependent view was named to sort *before* its base.
- Fall back to retry-until-stable for anything left: emit, collect failures, retry. This converges because the
  real dependency graph is a DAG, and it beats guessing.

**3. Two hard T-SQL constraints surface, and they affect every run — not just empty targets.**

- `CREATE SCHEMA`, `CREATE VIEW`, `CREATE PROCEDURE`, `CREATE FUNCTION` and `CREATE TRIGGER` must each be the
  **first statement in their batch**. Our script is deliberately one transaction with no `GO` separators, so
  these cannot be emitted literally — they must be wrapped as `EXEC sp_executesql N'CREATE VIEW …'`. On an
  incremental sync that is one or two statements; on an empty target it is hundreds, which is why the gap only
  becomes obvious here. The emitter needs this either way.
- Fresh `IDENTITY` columns start at 1. Seeding rows under `IDENTITY_INSERT` leaves the seed untouched, so the
  application's next insert collides with a seeded key. Every table seeded with explicit identity values needs
  a `DBCC CHECKIDENT (…, RESEED)` afterwards. This is invisible on incremental syncs and guaranteed to bite on
  a fresh database.

**What the tool will not do: create the database itself.** Filegroups, collation, recovery model and initial
sizing are DBA decisions, and a database created with the wrong collation is painful to undo. A missing
database is reported, not provisioned. A database that exists but is empty is fine to work with.

**Collation is checked as a precondition.** A target whose collation differs from the source changes string
comparison and therefore changes the row hashes the data compare depends on. Mismatch is reported before any
compare runs rather than producing quietly wrong diffs.

### FK map: a neighbourhood diagram, not an ER chart

The tool already holds the graph — FKs drive the topological order, parent closure for seeds, and the
"referenced by" warning on deletes. The diagram is a view onto that same graph, not new data.

**A whole-schema ER diagram is the failure mode to avoid.** 142 tables draws as unreadable spaghetti, which
is how most database diagram tools become decoration. So the unit is a *neighbourhood*: one focused table,
1–2 hops, with direction and depth controls. Parents are what must exist first; children are what breaks if
referenced rows are missing.

What makes it worth building rather than pointing at an existing ER tool is that it is **plan-aware**. Nodes
carry sync state — in plan, pulled in as a prerequisite, schema-only, unsatisfiable — so the question it
answers is not "what are this table's relations" but "if I sync this table at its current mode, what comes
along and what breaks". Edges carry FK nullability, because that is what decides whether an orphan is
tolerable or a hard failure, and no generic ER view surfaces it against a sync plan.

Two things fall out of it for free: FK cycles become visible instead of being an abstract emitter concern,
and the unsatisfiable-seed case gets somewhere to be explained rather than just refused.

Rendering: hand-authored inline SVG with a band layout computed in the component. React Flow + dagre was
the original plan and was dropped once built: a depth 1-3 neighbourhood is a handful of horizontal bands,
and two dependencies to place three rows of boxes is not a trade worth making. If an "all reachable" mode
is ever added, that is when a real layout engine earns its place. Mermaid `erDiagram` stays rejected — no
state overlay, no interaction, poor layout on graphs.

### Safety model

- `Safety:ReadOnlyServers` in config (seeded with `DBSERVER-PROD`). A target on that list can
  never be applied to — script generation still works.
- Apply requires the user to type the target database name to confirm.
- Every apply runs in one explicit transaction with `XACT_ABORT ON`; failure rolls back whole.
- Blast-radius guard: abort if deletes exceed a configurable share of the table's row count,
  unless explicitly overridden for that run.
- The connection panel shows source/target with a colour band by environment class so you cannot
  mistake which side is which.

## UI screens

1. **Connections** — source | target side by side, saved profiles, Test, environment badge.
2. **Overview** — object-type tree with counts, filter by Source only / Target only / Different / Same.
3. **Schema detail** — side-by-side DDL for the selected object, line-level diff.
4. **Data compare** — table picker with key selection, summary counts, then virtualized
   Inserts / Updates / Deletes tabs, per-row select, cell-level highlight on updates.
5. **Sync plan** — everything ticked across screens 3 and 4, rolled into one ordered script
   (FK-dependency ordered for whole-DB runs), with Download and the guarded Apply.
6. **Run log** — what was generated, what was applied, what the server said.

## Build order

1. Solution skeleton + Core model & diff engine + unit tests (no DB).
2. SQL Server schema reader + T-SQL emitter, verified against `DBSERVER-DEV` (Dev).
3. API endpoints + React shell + screens 1–3 (schema path end to end).
4. Volume readout (`sys.dm_db_partition_stats`) wired into connect + table list.
5. Data compare engine (key+hash), per-table modes, parent closure + screens 4–5.
6. Apply path + safety guards + screen 6.
7. PostgreSQL provider stub proving the abstraction holds.

## Open questions

- Where do saved connection profiles live? Leaning: `%APPDATA%\DbDelta\profiles.json`, passwords
  via Windows DPAPI, never in the repo.
- Resolved: whole-DB ordering is a topological sort with tables created bare and FKs added afterwards,
  so `NOCHECK` bracketing is no longer needed for cycles. See *Empty target*.
