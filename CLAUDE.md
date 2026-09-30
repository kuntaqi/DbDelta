# DbDelta — project context

Read this first. It records where the project stands, the decisions that are locked, and the conventions
every change follows.

## What this is

A tool that compares two databases — structure *and* data — and syncs one
direction. Three granularities: schema only, one table's data, or the whole database. Its reason to exist:
Microsoft's `sqlpackage`/DacFx already does schema compare for free, but free **data** compare with selective
per-table sync and real guardrails does not exist (Redgate Data Compare is paid).

## Status — read before writing any code

The app runs. `dotnet run --project src/DbDelta.Api` serves it on http://localhost:5199 once the SPA has
been built once (see *Running it*). All seven screens work against real databases: connect, survey a whole
instance, compare schema and data, pick what goes in the plan, review the script, apply it under guards,
read the run log.

**The Instance screen lists every database name on a server**, which is exactly the infrastructure topology
the naming constraint below exists to keep out of this repo. Never paste its output or screenshot it into
`docs/`, a mockup, or a test fixture; its tests assert containment, never the whole list. It also compares
two servers, and that mode prints both servers' whole inventories — same rule, doubled.

**Instance comparison matches database names, and matching names is not enough.** Where the environment is
part of the name — `AppProd` against `AppUat`, this repo's own convention — two servers share no names, so
name matching pairs nothing and the result reads like a complete list of differences when it is a list of
things nobody has lined up. Hence declared pairings, and hence the warning that fires when nothing matched.
Its verdicts also distinguish "we did not look" and "we could not look" from "the counts agree", and
`CountsMatch` explicitly does not mean the schemas match. Don't collapse any of that into a boolean.

**Nothing is selected for you.** Schema objects are ticked on Schema compare, table data is picked per
table on the data screen. An empty plan yields an empty script and a refused apply.

**The screens are tabs, not a wizard.** A compared pair of connections is the only prerequisite — except
Instance, which needs just a connection — and after that every screen is reachable from every other, in any
order. Docs still number the screens for reference; the app does not, because numbering implied an order it
never enforced.

**Schema compare is one screen, not two.** The object list and one object's difference used to be separate
screens (Overview and Schema detail); the difference now opens inline under the object's own row, so ticking
for the plan and reading the diff happen in the same place. One object is open at a time and its detail is
fetched when it opens — the emitter runs per object, so fetching all of them up front would be wasted work
on a database with hundreds of tables.

What exists:

- `docs/PLAN.md` — the engineering plan. Architecture, locked decisions, the diff engine design, safety
  model, and the open questions. This is the source of truth; read it in full before proposing anything.
- `docs/mockup/dbdelta-ux.html` — a 7-screen reference of the app **as built**, rendered HTML. Open it in
  a browser. It ends with a "Dirancang, tidak dibangun" ledger: ten ideas the first mockup drew as real
  that were never implemented. Do not treat the older mockup, or any screenshot of it, as evidence that a
  feature exists.

## Locked decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Engine | SQL Server first, provider-abstracted so PostgreSQL can slot in later |
| 2 | Interface | Local web UI — ASP.NET Core API (.NET 10) + React 19 + TypeScript + Vite |
| 3 | Sync execution | Always emit a reviewable `.sql` script; apply is opt-in, transactional, blocked on read-only servers |
| 4 | UI language | English |
| 5 | Grid/diagram libs | None. The grids are plain tables and the FK map is hand-authored inline SVG. TanStack and React Flow were planned, then dropped as unneeded at these sizes |

Angular was considered and rejected: React was expected to need TanStack for virtualized diff grids, and
the commercial component libraries that would have covered it are not an option for an open-source tool. The virtualization never
turned out to be necessary — row detail is capped at 200 rows because the two-pass design fetches only
what is shown.

## Naming constraint — do not violate this

Every example in `docs/` uses **generic stand-in names**, and that is intentional:

- `DBSERVER-PROD` / `DBSERVER-UAT` / `DBSERVER-DEV` for servers
- `AppProd` / `AppUat` / `AppDev` for databases
- `sales.vCompanySegment`, `dbo.Category`, `Segment` for schema objects

Production database hostnames and per-environment database names in a public repository are infrastructure
topology, not documentation, so no example here uses real ones.

**Never introduce real server names, database names, or internal object names from any organisation's
environment** — not in prose, sample SQL, screenshots, or test fixtures. Your own environment's patterns go
in the gitignored `appsettings.Local.json`. The docs look generic by design, so this constraint is invisible
from reading them, which is exactly why it is written down.

## Code conventions

- `async`/`await` for new backend code, even where surrounding code is synchronous.
- **File-scoped namespaces** (`namespace X;`) in all new `.cs` files — never block-scoped.
- **One class per file**, named after the type. No bundling DTOs/enums together.
- **No `dynamic` for a known request shape.** Define a typed request model, bind `[FromBody] TypedModel`.
- **Controllers stay thin** — they call a service, never a repository directly.
- Comments: terse and technical, explaining a non-obvious *why*. **No XML doc comments** (`/// <summary>`),
  no ticket-reference comments.
- Commits: one-line messages, no body, no `Co-Authored-By` trailer, no emojis, no AI/tool attribution
  anywhere that leaves the machine.

## Architecture at a glance

```
src/DbDelta.Core/          engine-neutral: model, diff, script assembly, safety
src/DbDelta.SqlServer/     provider: sys.* catalog reader, T-SQL emitter
src/DbDelta.PostgreSql/    provider: later
src/DbDelta.Api/           ASP.NET Core host: endpoints + OpenAPI, serves the built SPA
src/DbDelta.Web/           React 19 + TypeScript + Vite SPA
tests/DbDelta.Core.Tests/       diff engine + planning unit tests, no database needed
tests/DbDelta.Api.Tests/        connection parsing and safety classification, no database needed
tests/DbDelta.SqlServer.Tests/  integration against LocalDB, skipped when it is absent
```

`Core` never references a provider package. The API is the only thing that touches a database — the SPA
holds no credentials and issues no SQL.

## The seven ideas worth understanding before touching the design

These are covered fully in `docs/PLAN.md`; they are listed here so their existence is not missed.

1. **Data compare is two passes.** Stream `(key, row_hash)` from both sides and merge-join for exact counts
   in O(1) memory; fetch full rows only on demand. Pulling both tables into memory does not survive a
   real table.
2. **The sync plan is a cart of intents, never SQL fragments.** Scope lattice `Database ⊃ Table ⊃ Row`, with
   dedup by change-unit identity. That is what makes overlapping selections structurally unable to emit a
   statement twice. The schema screen offers two scopes — *Picked items* and *Entire database* — and under
   the second, unticking records a first-class `Exclusion` that re-escalation cannot undo. Scope covers the
   schema only; a table's rows enter the plan when that table is picked on the data screen. A picked table
   can then be narrowed to particular rows, but only under `AllRows` and only from the 200 the screen
   shows — so narrowing starts from nothing rather than from everything on screen, and the plan repeats
   "N of M changed rows" for as long as it lasts.
3. **Limited row modes must suppress deletes.** Under `Top N` or `Filter`, a row absent from the source only
   means "outside the limit" — honouring deletes would wipe the target while "seeding" it. Correctness rule,
   not a preference. The other half of the same rule is closure: a picked object pulls in what it references
   (`SchemaClosure`), and a written row pulls in the parent rows it points at (`ParentClosure`). Both are
   derived from the selection on every read, never stored in it, so unticking removes what it alone required.
4. **`CREATE VIEW`/`PROCEDURE`/`FUNCTION`/`TRIGGER`/`SCHEMA` must each begin a T-SQL batch.** The script is
   deliberately one transaction with no `GO`, so these need `EXEC sp_executesql` wrapping. Also
   `DBCC CHECKIDENT … RESEED` after seeding any identity table. And **the name a programmable is created
   under comes from the catalog, never from its own body** — `sp_rename` leaves the old name in
   `sys.sql_modules`, so the stored text lies about it for the rest of the object's life. The header is
   rewritten and nothing else in the body is, and the comparison canonicalises the header name away on both
   sides; either half alone leaves a compare that cannot converge. `ProgrammableHeaderReader` is in Core
   because both halves need it and Core cannot reference a provider.
5. **An index has a kind, and the kind decides the statement.** `IndexDefinition.Kind` comes from
   `sys.indexes.type_desc`; clustering stays separate because a columnstore is either. Spatial and XML
   indexes cannot be scripted from `sys.indexes` alone — the tessellation and the primary/secondary split
   come from `sys.spatial_indexes`, `sys.spatial_index_tessellations` and `sys.xml_indexes` — and each
   option is written **only when the catalog had it**, since an `AUTO_GRID` rejects `GRIDS` and only a
   geometry grid takes a `BOUNDING_BOX`. A columnstore reports its columns as *included* columns with key
   ordinal zero, which is why a nonclustered one reads them into `Columns` and a clustered one keeps none.
   The prologue sets `QUOTED_IDENTIFIER ON` because spatial and XML DDL is refused without it. What cannot
   be written is refused through `SyncScript.Refusals` and never as a no-op step, and `IndexEmitSupport` is
   the single place that decides.
6. **A filter predicate is checked by alphabet, and its function allowlist is drawn by determinism.** The
   predicate is embedded into two queries against two databases on two connections, so anything answering
   differently between them — `GETDATE()`, `DB_NAME()`, `FORMAT` — makes the merge join report rows as
   inserted and deleted when nothing changed. That, not injection, is why the list is short: reaching user
   code is already impossible because a scalar UDF must be schema-qualified and the alphabet rejects a dot.
   The list also holds only functions whose arguments are ordinary expressions, which is why `DATEADD`,
   `DATEPART` and `CAST` are out — each takes a bare word that is not a column, and admitting one would mean
   widening the alphabet. Don't add a name without checking it against both rules.
7. **A column difference is not always an `ALTER COLUMN`.** `ALTER COLUMN` changes a type, a nullability or
   a collation and nothing else, and never takes `IDENTITY`. A default-only difference moves the default, a
   computed one drops and re-adds the column, and identity or stored-to-computed rebuilds the table
   (`TableRebuild`) or is refused. Around any change, everything the server says holds the column comes
   down and goes back up — `ColumnDependents` is that list, and each entry on it was an error 5074 first. A
   foreign key between two changing tables is reached from both ends, so every such drop goes through
   `EmitContext.BracketForeignKey`. A schema-bound module is refused against, never dropped.

## Build order

1. Solution skeleton + Core model & diff engine + unit tests (no DB)
2. SQL Server schema reader + T-SQL emitter
3. API endpoints + React shell + screens 1–2 (schema path end to end)
4. Volume readout (`sys.dm_db_partition_stats`) wired into connect + table list
5. Data compare engine, per-table modes, row-level parent closure + screens 3–4
6. Apply path + safety guards + screen 5
7. FK map (screen 6)
8. PostgreSQL provider stub proving the abstraction holds

## Running it

```
cd src/DbDelta.Web && npm install && npm run build   # builds into ../DbDelta.Api/wwwroot
cd ../.. && dotnet run --project src/DbDelta.Api     # http://localhost:5199
```

For UI work run the API and `npm run dev` (port 5200) side by side; Vite proxies `/api` to 5199.
`wwwroot/` is build output and is not committed, so a fresh clone must run the npm build once before
`dotnet run` serves anything.

Two demo databases on LocalDB make the UI worth looking at — recreate them from the script in
`docs/demo-data.sql` if they are missing.

**There is a second, unverified setup path.** `Dockerfile` and `docker-compose.yml` exist because the plan
is to publish this with two ways in — run the projects, or run it in Docker. **Neither file has ever been
built.** This machine is a Hyper-V guest with no nested virtualisation, so no Docker daemon can run on it;
that is a hypervisor setting, not something installable from inside. Do not describe the Docker path as
working, and do not quietly fix it up without a daemon to test against. Two things about it are true
regardless: a Linux container has no Windows identity, so Windows authentication is unavailable there, and
LocalDB is local-only, so a container cannot reach it at all — which is why the compose file brings its own
SQL Server. Full reasoning in `docs/PLAN.md`, *Going open source, and two ways in*.

**The API refuses to start if it would listen beyond loopback.** DbDelta has no authentication, so the
address it binds is the whole of its access control: anyone who can open the page can apply changes to any
database the process can reach. `NetworkExposureGuard` checks the configured URLs before the host is built —
so a refusal happens before anything binds — and `Safety:AllowRemoteAccess` turns the refusal into a warning
rather than into silence. The one exception is a container, which has to bind all interfaces to be reachable
through a published port at all; there the guard defers and says what it could not check, and the port
publish is the real control. Don't "fix" a refusal by setting the flag in committed config.

**Where state lives is configurable now.** Saved profiles and the run log resolve to
`DbDelta:DataDirectory` (`DbDelta__DataDirectory` as an environment variable) and fall back to
`%APPDATA%\DbDelta` when it is unset. The fallback is what a direct run gets; the setting is what makes a
mounted volume possible, since a path nobody can predict cannot be mounted.

`src/DbDelta.Api/appsettings.Local.json` is gitignored and overrides `Safety`. Real server-name patterns
for environment classification and the read-only list belong there, never in the committed defaults: a
real environment's naming scheme is not something this repo should carry. Without it the shipped
patterns only recognise `*-DEV` / `*-UAT` / `*-PROD` and LocalDB.

## Environment notes

- Requires the .NET 10 SDK and Node 20+. `sqlcmd`/`bcp` (ODBC 17) are useful for verification.
- `sqlpackage` is not assumed present.
- **Integration tests run against SQL Server LocalDB**, never a shared server. Connection:
  `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`.
  `tests/DbDelta.SqlServer.Tests` creates `DbDelta_IntegrationTest_Src` / `_Tgt` per run and drops them
  after. If LocalDB is missing the tests **skip** rather than fail, so `dotnet test` stays green on a
  machine without SQL Server — check for skips before believing a clean run proves the reader works.
  Start it with `SqlLocalDB start MSSQLLocalDB` if a test reports it unavailable.
- **The integration tests are split by cost, and `dotnet test` still runs all of them.** The inner loop is
  the fast half:

  ```
  dotnet test --filter "Speed!=Slow"    # ~6s: everything except the database-creating tests
  dotnet test                            # ~50s: the whole thing, before committing
  ```

  `Speed=Slow` marks the classes that create their own database, move the 1200-row fixture table, or walk
  every database on the instance. The line is exactly *"does this test need more than the shared source and
  target pair"*, so which side a class belongs on is readable from the test itself — a class with no
  `CreateScratchTargetAsync` / `CreateEmptyTargetAsync` call belongs in the fast half.
  **The fast half is not proof.** It is 60 of 125 SqlServer tests and applies no script to any database, so
  it cannot tell you a script runs. Run the whole suite before committing.

  `SpeedTraitTests` holds the split honest: it fails if a class that takes the LocalDB fixture is not marked
  slow, or if one is marked slow without needing a database. A trait is a string in an attribute, and a
  misspelled one would drop its tests out of *both* filters at once.
- **The integration classes are spread over four xUnit collections, which is what makes them run in
  parallel.** Membership is balanced by measured cost, so a class's collection says nothing about what it
  tests. The shared source and target pair is built once per process and reference-counted — do not give a
  collection its own fixture instance, and do not write to the shared pair from a test; anything that writes
  creates its own scratch database, and those names must stay unique across the whole suite. Two classes
  enumerate every database on the instance, which takes the same server-level metadata locks as the `CREATE`
  and `DROP DATABASE` going on in the collections beside them. Those reads run through
  `LocalDbFixture.WithQuietInstanceAsync`, which holds the churn still for their duration rather than
  retrying the collision — the retry it replaced caught error 1205 but not the other way the engine ends
  the same conflict, which is killing the losing session. Every `CREATE`/`DROP DATABASE` the fixture issues
  must go through `ExecuteOnMasterAsync` or the gate does not see it.
- LocalDB caps a database at 10 GB and accepts local connections only, so volume behaviour at real
  scale cannot be proven here — only that the queries are correct.
- Connection profiles live in `%APPDATA%\DbDelta\profiles.json`, never in the repo, and **store no
  password** — server, port, database, auth mode, username and the trust flag only. A SQL login password is
  asked for each session and kept in memory for that session. The rule is held by the request shape:
  `SaveProfileRequest` has no password field, and a pasted connection string is taken apart rather than
  stored. `.gitignore` excludes `profiles.json`, `recent.json`, `appsettings.Local.json`, `runs/` and
  `*.sync.sql`.
- **`recent.json` holds the pairs that compared successfully**, beside the profiles and under the same rule:
  `ComparedEndpoint` has no password field, so nothing has to remember to strip one. It is written after a
  comparison succeeds, so a pair that could not connect is never offered back, and writing it can never be
  the reason a compare fails — a corrupt or unwritable file reads as "nothing remembered". A pair is
  directional and deduplicated by (source server, source database, target server, target database); the
  decomposition of a pasted connection string is shared with the profile store in `ConnectionEndpoint`.
- Never point a write operation at a production server. `Safety:ReadOnlyServers` in config exists for this
  and its apply path is hard-blocked, not merely warned.
