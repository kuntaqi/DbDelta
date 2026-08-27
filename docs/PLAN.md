# DbDelta — database compare & one-way sync

Personal tool. Purpose: compare two databases (schema + data) and sync one
direction, at three granularities — schema only, one table's data, or the whole DB.

This is a design document, so it describes things that are not built. Where that is true it now says so at
the point it comes up, and the full list is in *Designed, not built* at the end. `docs/mockup/dbdelta-ux.html`
carries the same ledger for the screens. Neither document should be read as evidence that a feature exists.

## Decisions (locked)

| # | Decision | Choice |
|---|----------|--------|
| 1 | Engine | SQL Server first. Provider-abstracted so PostgreSQL slots in without touching the diff engine. |
| 2 | Interface | Local web UI: ASP.NET Core API (.NET 10) + React 19 SPA in TypeScript, Vite. |
| 3 | Sync execution | Always emit a reviewable `.sql` script. Apply is opt-in, transactional, and hard-blocked on read-only servers. |
| 4 | UI language | English. |
| 5 | Grid/diagram libraries | None. The grids are plain `<table>`s; the FK map is hand-authored inline SVG. |

Why React over Angular: the expectation was virtualized diff grids and tree tables, and TanStack Table +
TanStack Virtual were the best free answer to exactly that — React-first, with a much younger Angular
adapter. Kendo is licence-bound so it was off the table either way, which under Angular would have meant
hand-building the grid. This is a single-user local tool, so Angular's structure buys nothing here.

The virtualization never turned out to be needed, so TanStack was dropped before it was added — `package.json`
carries `react` and `react-dom` and nothing else. Row detail is capped at 200 rows because the two-pass design
fetches only what is shown, and 200 rows render fine in a plain table. The argument that picked React no longer
holds on its own terms; the choice still stands, for the ecosystem rather than for the grid.

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
  tests/DbDelta.Core.Tests/       diff engine + planning unit tests (no DB needed)
  tests/DbDelta.Api.Tests/        connection parsing and safety classification (no DB needed)
  tests/DbDelta.SqlServer.Tests/  integration against LocalDB, skipped when it is absent
```

The API is the only thing that touches a database; the SPA holds no credentials and issues no SQL.
The SPA's request and response types are hand-maintained in `src/DbDelta.Web/src/api.ts`. Generating them
from the API's OpenAPI document was the intent and is still the better answer — a changed response shape
would break the build instead of failing silently at runtime — but no generator is wired up, so today a
contract change has to be mirrored by hand in two places.

`Core` never references a provider package. Everything engine-specific sits behind:

- `IDatabaseProvider` — factory for everything below, plus `Key` and `Quoter`. No capability flags: nothing
  has needed to branch on one while SQL Server is the only provider, and inventing them before a second
  engine exists would be guessing at what differs
- `ISchemaReader` — reads the neutral schema model
- `IRowHashReader` / `IRowDetailReader` — one streams key+hash pairs, the other fetches full rows on demand.
  Two interfaces rather than the single `IRowSetReader` first sketched: the two passes share nothing but the
  table they read
- `IVolumeReader`, `ITableFingerprintReader`, `IKeyUniquenessChecker` — the volume readout, the whole-database
  scan, and the keyless-table key picker; each is engine-specific for the same reason
- `IScriptEmitter` / `IDataScriptEmitter` — turn a diff into DDL and DML for that engine, including how that engine handles statements
  that must begin their own batch (T-SQL `CREATE VIEW`/`PROCEDURE`/`SCHEMA` need `EXEC sp_executesql` wrapping
  inside a single-transaction script; PostgreSQL does not)
- `IIdentifierQuoter` — `[x]` vs `"x"`

### Neutral schema model

`Table`, `Column`, `PrimaryKey`, `UniqueConstraint`, `Index`, `ForeignKey`, `CheckConstraint`,
`View`, `Routine` (proc/function), `Trigger`, `Sequence`. Each carries a `ProviderExtras` bag for
engine-only attributes (filegroup, fillfactor, collation…) so the reader loses nothing even though the
comparer ignores what it doesn't understand.

Two entries in that first list are not what they looked like. A default is a property of its column
(`ColumnDefinition.DefaultConstraintName` plus the expression), not a top-level object, so it is compared as
part of the column rather than on its own. `UserDefinedType` exists in the `ObjectType` enum and nowhere
else — the reader does not read UDTs and `DatabaseSchema` has no collection for them, so a database using
them compares as if they were not there. That is a gap, not a decision.

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

Selections accumulate across screens — tick objects on Schema compare, pick tables and their mode on the
data screen, and both land in one plan. There is no separate "add to plan" step to forget. Three properties
make it more than a passive basket:

- **Closure, not literal selection.** Ticking `dbo.Company` pulls in what it needs: `dbo.Category` goes
  first because of `FK_Company_Category`. The plan therefore contains more than was clicked, and both the
  schema screen and the plan screen name what was added and why — a silently larger plan is a trap. An
  object pulled in shows `required by dbo.Company` on its own row, where the tick it did not get would be.
  See *What closure follows* for what counts as a prerequisite.
- **Tool-decided order.** Steps are topologically sorted by dependency, not by click order — FKs for tables,
  `sys.sql_expression_dependencies` for views and routines, since those depend through their SQL bodies rather
  than through constraints. Tables are created bare and their FKs added afterwards, which avoids FK cycles
  outright. This is what lets the whole plan run in one transaction. See *Empty target* below for the full order.
- **Carts go stale.** You compare at 14:02, pick for ten minutes, meanwhile someone deploys to UAT.
  Before applying, the plan re-verifies that the target still matches what was compared. A drift aborts the
  apply and asks for a fresh compare rather than running against a database it no longer understands.
  **Built for schema only:** `ApplyService.DriftAsync` re-reads the target schema and re-compares it against
  the schema captured at compare time. Row hashes for the affected keys are not re-checked, so a target whose
  *rows* moved under a selected table still applies. The fingerprint reader that would answer this already
  exists — it powers the whole-database scan — so this is wiring, not new machinery.

Plan state lives in the API session (`CompareSessionStore`), not only in browser memory, so a refresh doesn't
lose the picking work. It does not survive an API restart, and is not meant to.

### What closure follows

`SchemaClosure` walks four kinds of reference and adds what the target does not already have:

| Picked object | Follows | Because |
|---|---|---|
| A table being created | every foreign key's referenced table | the FK is added at the end of the script and fails if the parent is not there |
| A table being altered | only the foreign keys being *added* | the rest were satisfied when the target was built |
| A view or routine | everything its body reads — tables included | `CREATE OR ALTER` compiles the body, so a missing table fails the statement, not just the order |
| A trigger | the table it sits on | a trigger cannot be created before its table |

Two decisions inside that are worth stating, because both could reasonably have gone the other way.

**Only what is missing, not everything that differs.** A parent that exists on the target but differs is
left alone. The reference resolves against what is already there, so pulling it in would grow the plan
with alterations nobody asked for — a view over ten slightly-stale tables would drag all ten in. The one
exception is a parent that lacks the exact columns the key points at: that is a missing object wearing a
different shape, and the ALTER that adds the column has to be in the plan.

**Derived, never stored.** Prerequisites are recomputed from the ticks on every read rather than written
into the selection. Writing them in would mean unticking `dbo.Company` leaves `dbo.Category` behind in the
plan, with nothing left to explain why it is there.

A prerequisite the comparison cannot supply — a key pointing at a table missing from the source too — is
reported rather than dropped. Nothing can be emitted for it, so the script will fail on that FK, and saying
so up front is the only warning available.

This also changed what the schema reader captures. `sys.sql_expression_dependencies` was filtered to
programmable objects on both sides, because emission order was the only consumer and tables are created in
an earlier phase anyway. Closure needs the table edges too, so the referenced side now includes `U`. The
topological sort is unaffected: it already ignores dependencies outside the set it is sorting.

### Scope escalation: picking rows, then deciding "just do the whole database"

The cart must never hold SQL fragments. If it did, escalating to whole-database scope would leave the
earlier per-table fragments in place and emit duplicate `INSERT`s. So the cart holds **intents** —
`(scope, object identity)` — and SQL is compiled exactly once, at the end, from the normalized set.

Normalization runs over a scope lattice, `Database ⊃ Table ⊃ Row`. Selecting an ancestor collapses every
descendant selection beneath it: they are dropped as already covered, not merged. Change units are then
deduplicated by identity — `(objectType, schema, name, changeKind)` for schema, `(schema, table, keyTuple,
operation)` for data — so overlapping scopes are structurally incapable of producing the same statement
twice. The guarantee comes from the identity key, not from care at the call site.

**Status: built in `Core`, not reached from the app.** `PlanSelection`, `SelectionScope`, `PlanNormalizer`,
`ChangeUnitId`, `Exclusion` and `PlanCompiler` all exist and are covered by `PlanCompilerTests`, but the only
callers are those tests and one integration test. The API composes its script a simpler way: a
`HashSet<ObjectIdentity>` of ticked schema objects goes straight to the emitter's `include` filter, and a
`Dictionary<ObjectIdentity, DataSelection>` drives the data steps. Two consequences follow. There is no
`Database` scope to escalate *to* — the UI offers "select all differing", which ticks each object individually
and is not the same thing. And the dedup guarantee is currently provided by the set, which holds for schema
objects but has never been exercised at row scope, because nothing selects rows.

**Escalation is not a pure superset, and that is the interesting case.** Positive picks are subsumed
safely. Negative ones are not:

- an unticked `DROP dbo.SegmentLegacy`
- a deliberately skipped delete of `CompanyId 908`, still referenced by 12 rows in `dbo.Site`
- 128 of 130 rows taken, because 2 were wrong

A naive reading of "whole database" pulls all three back in — which silently overrides judgment already
exercised. So exclusions are stored as **first-class negative entries**, not as the absence of a tick, and
they survive escalation. Escalating means "also take everything I haven't considered", never "forget what
I decided". The UI lists surviving exclusions so they can be revoked deliberately.

That last sentence is design, not description. `Exclusion` is a Core type with tests proving it survives a
`Database` selection; no endpoint creates one, the session has nowhere to keep one, and no screen shows or
revokes one. Nothing is lost by that today — with no escalation control there is nothing for an exclusion to
survive — but the two have to arrive together, because escalation without exclusions is precisely the silent
override this section exists to prevent.

One consequence worth stating: an exclusion is only meaningful against a known change. If a later compare
surfaces a *different* change to the same object, that is a new change unit with a new identity and it is
not covered by the old exclusion.

### Nothing enters the plan without being picked

Earlier drafts selected schema differences automatically and left data opt-in. That is now uniform:
**nothing** is selected for you. Schema objects are ticked on Schema compare, table data is picked on the
data screen, and an empty plan produces an empty script and a refused apply that says why.

The reason is that a tool which pre-selects everything it found makes the review step optional in
practice — you would press apply on a list you never read. "Select all differing" exists as one click for
when that is genuinely what you want; the difference is that it is a click.

Within a picked table, that table's changes go in under its chosen mode. Selecting every difference the
compare happened to find would make the heaviest part of a sync the least deliberate one.

Within the script, deletes walk the foreign key graph child-first and inserts parent-first, so the two
cannot share a phase. `INSERT` batches stop at 1000 row constructors because that is the T-SQL ceiling,
and any table whose rows were written with explicit identity values gets `DBCC CHECKIDENT … RESEED`
afterwards — without it the application's next insert collides with a row just seeded.

Row deletes count as destructive alongside dropped objects, and a delete share over `Safety:MaxDeleteShare`
joins the same list rather than sitting in a warning nobody has to acknowledge.

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
script first" premise. So the script is measured against `Safety:MaxReviewableScriptBytes` (100 MB) and
reported as oversized rather than being handed over as a 400 MB file nobody can read.

**The staged bulk path behind that guard is not built.** `SqlBulkCopy` on apply and `BULK INSERT` + a data
file for download were the intended other half; today the threshold produces a flag and the script is still
emitted in full. Two things are worth separating here. The measurement is also not an estimate — the whole
script is assembled first and then counted, so an oversized plan has already paid for the string it warns
about. A genuine up-front estimate is cheap (`row count × column count × an average literal width` is already
computed for the transfer readout) and would let the tool refuse before building. That ordering matters more
than the bulk path does: without it, the guard protects the person reading the script but not the process
generating it.

### Per-table data modes: seeding instead of copying everything

Data scope is chosen per table, not once for the whole run:

| Mode | Meaning | Insert | Update | Delete |
|------|---------|--------|--------|--------|
| `All rows` | full compare | yes | yes | yes, blast-radius guarded |
| `Top N` | first N rows by primary key ascending | yes | yes | **suppressed** |
| `Filter` | rows matching a `WHERE` predicate | yes | yes | **suppressed** |
| `Schema only` | structure, no data | — | — | — |

`Filter` is implemented end to end in the backend — `DataCompareRequest.FilterPredicate` reaches the row-hash
reader's `WHERE` clause, and `SuppressDeletes` already covers it — but the data screen offers only `All rows`,
`Top N` and `Schema only`, so there is no way to type a predicate and the mode is unreachable. It is a text box
away, and it is deliberately still absent: a raw predicate concatenated into the reader's SQL is the one place
in this tool where user text becomes SQL, and it needs a decision about that before it gets a control.

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

**Half built.** `TableDependencyGraph.ParentClosure` exists and is tested, but the only caller is its test —
`DataStepsAsync` emits exactly the tables that were picked, in dependency order, and pulls in no parent rows.
So `Top N` on a child table today produces inserts that can fail the FK on apply, which the transaction then
rolls back whole. Failing loudly inside a transaction is the tolerable version of this bug, not a defence of
it. The closure is the missing call; reporting unsatisfiable FKs is the missing message.

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

**Collation is checked, but as a warning rather than a precondition.** A target whose collation differs from
the source changes string comparison and therefore changes the row hashes the data compare depends on. The
mismatch is detected and surfaced — `CompareService` compares the two `DATABASEPROPERTYEX` values and adds a
warning saying data compare results cannot be trusted — but it rides along with the schema comparison's other
warnings and stops nothing. A data compare on a collation-mismatched pair still runs and still returns diffs.
"Reported before any compare runs" is what this should be; today the person has to read the warning and decide.

### FK map: a neighbourhood diagram, not an ER chart

The tool already holds the graph — FKs drive the topological order, parent closure for seeds, and the
"referenced by" warning on deletes. The diagram is a view onto that same graph, not new data.

**A whole-schema ER diagram is the failure mode to avoid.** 142 tables draws as unreadable spaghetti, which
is how most database diagram tools become decoration. So the unit is a *neighbourhood*: one focused table,
1–3 hops. Parents are what must exist first; children are what breaks if referenced rows are missing.
Focus and depth are adjustable; the **direction filter (Parents / Children / Both) is not built** — both
directions are always walked and drawn. At depth 1–2 that is the sensible default and the filter would be
noise; at depth 3 on a hub table it is the difference between a diagram and a smear.

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
- Blast-radius guard: a delete share over `Safety:MaxDeleteShare` (5%) does not get its own abort. It joins
  the destructive list alongside `DROP TABLE`, `DROP COLUMN` and `DELETE FROM`, and that whole list is what
  `AllowDestructive` has to acknowledge. One acknowledgement covers everything on it — which is the deliberate
  part (an over-limit delete is not a different *kind* of consent from a drop) and the weak part (the count
  is what you acknowledge, not each item).
- The connection panel shows source/target with a colour band by environment class so you cannot
  mistake which side is which.

All five are built. The destructive list is matched by scanning emitted SQL for those three strings rather
than by asking the step what it is — `ScriptStep` carries a `Phase` and a description but no destructive flag,
so a step that drops something without those words in it would pass unnoticed. No emitter currently produces
one; the fragility is in the matching, not in a known hole.

## UI screens

They are numbered here for reference only. In the app they are tabs, not steps: a compared pair of
connections is the only prerequisite, and after that every screen reaches every other in any order.

1. **Connections** — source | target side by side, Test, environment badge. Built, including connection-string
   and full-detail entry with port and SQL login. **Saved profiles are not built** (see *Connection profiles
   carry everything except the secret*) — the fields are retyped every session, which is the friction that
   section exists to remove.
2. **Schema compare** — object-type tree with counts and per-object ticks; clicking a row opens that
   object's difference inline beneath it: one table of what differs with both sides' values, side-by-side
   DDL with changed lines marked, and the statements that will run on the target.
3. **Data compare** — table picker with key selection and per-table mode, whole-database scan, summary counts,
   then **one combined grid with a `Change` column** rather than separate Inserts / Updates / Deletes tabs, and
   cell-level highlight on updates. Not virtualized and not per-row: the grid caps at 200 rows because the
   two-pass design only fetches what is shown, and selection is per table, so there is nothing for a row
   checkbox to add. `Row` scope exists in the cart model and has no control.
4. **Sync plan** — everything ticked across screens 2 and 3, rolled into one ordered script
   (FK-dependency ordered for whole-DB runs), with Download and the guarded Apply.
5. **Run log** — what was generated, what was applied, what the server said.
6. **FK map** — foreign-key neighbourhood of one table, as inline SVG.

### Overview and Schema detail are one screen

They were built as two, and the split cost something on every use: the list was where you ticked objects
for the plan, the detail was where you found out whether an object deserved ticking, and each answer meant
leaving the other screen. Deciding about ten objects meant twenty navigations, and the list lost its place
each time.

The difference now opens inline beneath the object's own row. The row stays visible, so the tick and the
evidence for it sit together, and the list never moves under you. One object is open at a time, and its
detail is fetched when it opens rather than with the comparison — the emitter runs per object, so building
all of them up front would be work nobody asked for on a database with hundreds of tables.

Two smaller consequences:

- **Ticking and opening are separate acts.** The checkbox never opens the panel; the rest of the row never
  ticks. Conflating them would make a scan through the list add things to the plan.
- **One table, not two.** The old detail screen listed what differed, then listed property values
  separately, so "Segment differs" and "NVARCHAR(40) vs NVARCHAR(20)" were two lookups. Each difference is
  now one row carrying both sides' values. `Definition` is the exception: it holds a whole view or routine
  body, and it is already rendered side by side below, so repeating it inside a table cell would bury every
  other row under it.

The navigation cost of the split was also the last reason for a tab that could be disabled mid-session:
Schema detail had to be locked until an object had been opened. With one screen, a compared pair of
connections is the only prerequisite anywhere in the app.

## Build order

1. **Done.** Solution skeleton + Core model & diff engine + unit tests (no DB).
2. **Done.** SQL Server schema reader + T-SQL emitter, verified against LocalDB rather than a shared Dev
   server — `tests/DbDelta.SqlServer.Tests` creates and drops its own databases per run, and skips when
   LocalDB is absent.
3. **Done.** API endpoints + React shell + screens 1–2 (schema path end to end).
4. **Done.** Volume readout (`sys.dm_db_partition_stats`) wired into connect + table list.
5. **Partly.** Data compare engine (key+hash) and per-table modes + screens 3–4 are done; **parent closure is
   not wired**, and `Filter` mode has no control.
6. **Done.** Apply path + safety guards + screen 5, with a schema-only drift check.
7. **Done.** FK map (screen 6), as hand-authored inline SVG.
8. **Not started.** PostgreSQL provider stub proving the abstraction holds. `src/DbDelta.PostgreSql/` does not
   exist yet, so decision 1's "provider-abstracted" claim rests on the shape of the interfaces and has never
   been tested by a second implementation.

## Open questions

- Resolved: saved connection profiles live in `%APPDATA%\DbDelta\profiles.json` and **store no password
  at all**. The earlier leaning — passwords encrypted with Windows DPAPI — is dropped. See *Connection
  profiles carry everything except the secret*.
- Resolved: whole-DB ordering is a topological sort with tables created bare and FKs added afterwards,
  so `NOCHECK` bracketing is no longer needed for cycles. See *Empty target*.

### Connection profiles carry everything except the secret

Retyping a server, a port, a database name and an auth mode every session is the largest avoidable
friction in the tool, and a profile fixes it. A stored password does not belong in that trade.

**This is a resolved decision, not a built feature.** Nothing writes or reads `profiles.json` today; the
connections screen is retyped every session. What is settled is the shape, so that when it is built there is
nothing left to decide.

So a profile holds the **non-secret** half of a connection — server, port, database, auth mode, and the
trust-certificate flag — in `%APPDATA%\DbDelta\profiles.json`, never in the repo (`.gitignore` already
excludes it). Under Windows auth that is the whole connection and nothing is missing. Under a SQL login
the password is asked for each session and kept in memory for that session only.

An earlier draft leaned on Windows DPAPI to encrypt the password into the same file. That is dropped.
DPAPI protects the file against another user on the same machine; it does nothing about the case that
actually matters here — a tool that writes to production-adjacent servers holding a credential it can
replay without anyone present. Retyping a password is a few seconds against a risk that lasts as long as
the file does. "The repo does not contain it" was never the right test, either: the disk is the concern,
not the repository.

Two consequences worth stating:

- **Recently-compared pairs are derivable, profiles are not the same thing.** The run log already persists
  to `%APPDATA%\DbDelta\runs\*.json`, so a "recently compared" list can be built from what actually ran
  without storing anything new. A profile is a saved *intent* to connect; a run is a record that one
  happened.
- **A profile is not a session.** Compare sessions — including key overrides and plan picks — live in API
  memory and are lost on restart. A profile shortens the retyping; it does not restore a comparison.

### The whole-database scan is a screen, not a compare

"Which tables differ" needs every table compared, which is expensive enough that it is a button rather
than something that happens on screen load. Three attempts, measured against a real 263-table database
(165 tables under the 200 MB limit):

| Approach | Time | Why |
|---|---|---|
| Stream key+hash per table, merge-join in the client | 126 s | pipelines well, but hashes every row with SHA2 |
| Aggregate server-side with SHA2, two slices per row | 311 s | *slower* — SHA2 evaluated twice per row, and no pipelining |
| Aggregate with one `BINARY_CHECKSUM` per row via `CROSS APPLY` | **4.4 s** | the cost was server CPU, not the wire |

The middle row is the useful one: the first guess was that network transfer dominated. It did not. Server
CPU did, and computing the digest twice per row made the "optimisation" 2.5× worse than the thing it
replaced. Only measuring found that.

`BINARY_CHECKSUM` is weaker than a cryptographic hash and can collide. That is the right trade **because
this is a screen**: it decides which tables are worth looking at, and the exact streaming merge join still
runs on whichever table is opened. On the database above it reached the identical verdict as the SHA2 pass
— 37 differing, 128 matching — which is evidence, not proof.

The size limit stays. With no limit the same scan runs past nine minutes: 17 large tables dominate, and no
cheaper digest fixes reading 21 GB. Skipped tables are reported as skipped with their size, never folded
in with the matching ones.

### Tables without a primary key wait for a key, they are not guessed at

On a real 263-table database, 78 tables have no primary key — by far the largest reason data cannot be
compared, ahead of 17 skipped for size and 3 that exist only on the source. The first mockup said those
tables would be "flagged, waiting for you to pick key columns manually". The flag was built; the picker
was not, so 78 tables were permanently unavailable and clicking one blanked the pane with no explanation.

The picker now offers the columns present on both sides, and **verifies the choice before accepting it**:
row count against distinct key count, plus a NULL check, on the source *and* the target. Both sides matter
because a key that is unique on the source and repeated on the target still breaks the merge join — and it
is the target that gets written to.

This turns a confusing mid-compare failure into a refusal that carries the numbers:

> Source: 453 rows but only 13 distinct key values, so the key does not identify a single row.

Falling back to "use every column as the key" was rejected. It looks like it works until duplicate rows
make it silently wrong, and a data sync that is silently wrong is worse than one that refuses.

### "No primary key" is two different situations

The picker treated every keyless table the same, and that was wrong in both directions. Guessing a column
took three tries on a real table — the first refused at 453 rows against 13 distinct values, the second at
14, the third finally accepted — while a table that already carried a perfectly good key was made to go
through the same guessing, because the key simply was not the primary key.

**A key the schema already states.** A `UNIQUE` constraint, or a unique index over columns that are all
`NOT NULL`, is a key. The schema reader had been reading both all along; the picker ignored them. Now they
are offered by name, no scan needed:

> No primary key, but `UQ_Ledger_Ref` already declares `Ref` unique. Confirm it to compare this table's data.

A nullable unique index is deliberately not counted. SQL Server permits one NULL row under one, and that is
exactly the row that cannot be addressed by key.

**Nothing declared.** Then every candidate is measured in a single pass — `COUNT(DISTINCT col)` and a null
count per column, capped at 40 columns, with `text`/`ntext`/`image`/`xml`/`geography`/`geometry`/
`hierarchyid` left out because `COUNT(DISTINCT …)` refuses them outright and one such column would fail the
whole query rather than its own measurement. Each column then carries what was actually found —
`unique across all rows`, `2 distinct of 3 rows`, `1 NULL row(s)` — so the choice is made from evidence
instead of from three rejections in a row. A probe that cannot run at all is reported and the picker stays
usable by hand.

Uniqueness is only ever claimed of the rows that exist right now. A column unique today can start merging
rows tomorrow, so the confirmed choice is still verified on both sides at the moment it is confirmed — the
profile decides what to *offer*, never what to accept.

What did not change: **nothing is compared until the user confirms.** The suggestion arrives pre-ticked so
nobody has to guess, and the confirming click is still theirs. An empty table recommends nothing at all —
every column is trivially "distinct per row" at zero rows, and accepting that would hand back a key chosen
by an empty table and then apply it to a full one.

One consequence reaches the table list: a table whose key is merely undeclared now reads `key not set`
rather than `no key`, because those are different problems and only one of them is the user's to solve.

## Designed, not built

Everything above that is design rather than description, in one place. `docs/mockup/dbdelta-ux.html` carries
the screen-level version of this list; this one is the engineering side, so the two overlap without being the
same list. Each row says where the design lives in the code, because in most cases the piece exists and only
the call site is missing — which is what makes them cheap and also what makes them easy to mistake for built.

| Idea | Where the design already lives | What is actually missing |
|---|---|---|
| Scope escalation, `Database ⊃ Table ⊃ Row` | `PlanNormalizer`, `SelectionScope`, `PlanCompiler`, all tested | No API or UI calls them; the app composes its script from a `HashSet` of ticks instead |
| First-class exclusions with revoke | `Exclusion`, `CompiledPlan.AppliedExclusions`, tested | Nothing creates one, the session cannot hold one, no screen shows one |
| Parent closure for seeded tables | `TableDependencyGraph.ParentClosure`, tested | Never called from the data path, so `Top N` on a child can emit inserts that fail their FK |
| Row hashes in the drift check | `ITableFingerprintReader`, used by the whole-database scan | `DriftAsync` re-reads schema only; moved rows under a selected table do not abort the apply |
| Staged bulk path past the size cliff | `MaxReviewableScriptBytes`, measured and flagged | No `SqlBulkCopy` / `BULK INSERT` path, and the measurement happens after the whole script is built rather than before |
| Collation as a precondition | `CompareService` detects and warns | The warning stops nothing; a mismatched pair still compares data |
| `Filter` row mode | `FilterPredicate` reaches the reader's `WHERE`; deletes already suppressed | No input control — and it needs a decision about user text becoming SQL before it gets one |
| Row-level selection | `SelectionScope.Row` exists in the cart model | Selection stops at the table, by decision; listed here because the model implies more than the UI offers |
| Saved connection profiles | Shape settled: `%APPDATA%\DbDelta\profiles.json`, no password | Nothing reads or writes the file |
| `UserDefinedType` | A member of the `ObjectType` enum | No reader, no model type, no collection on `DatabaseSchema` — UDTs compare as absent |
| Direction filter on the FK map | `Walk` already takes a direction, called twice | No control; both directions are always drawn |
| Generated TypeScript from OpenAPI | The API serves an OpenAPI document | `api.ts` is hand-maintained, so a contract change has to be mirrored twice |
| PostgreSQL provider | The provider interfaces | `src/DbDelta.PostgreSql/` does not exist; the abstraction has never met a second engine |

One of these is a correctness gap rather than an absent convenience: parent closure for seeded rows can
produce a script that fails on apply. The transaction rolls it back, so the failure is loud, but a tool
whose premise is "review the script first" should not be emitting scripts it could have known were
incomplete. Schema closure was the other one and is now built — the same argument applies to what is left.
