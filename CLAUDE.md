# DbDelta — project context

Read this first. It records where the project stands, the decisions that are locked, and the conventions
every change follows.

## What this is

A personal tool (not a company project) that compares two databases — structure *and* data — and syncs one
direction. Three granularities: schema only, one table's data, or the whole database. Its reason to exist:
Microsoft's `sqlpackage`/DacFx already does schema compare for free, but free **data** compare with selective
per-table sync and real guardrails does not exist (Redgate Data Compare is paid).

## Status — read before writing any code

**No application code exists yet. This is deliberate, not an omission.**

The working order on this project is: understand → survey → write the plan → mock the UX → get sign-off →
*then* code. As of the last session we are at **mockup awaiting sign-off**. Do not start scaffolding
projects or writing C#/TSX until that sign-off happens.

What exists:

- `docs/PLAN.md` — the engineering plan. Architecture, locked decisions, the diff engine design, safety
  model, and the open questions. This is the source of truth; read it in full before proposing anything.
- `docs/mockup/dbdelta-ux.html` — a 7-screen UX mockup, rendered HTML (not images). Open it in a browser.
  It doubles as the spec: the annotations in it record which decisions still need confirming.

## Locked decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Engine | SQL Server first, provider-abstracted so PostgreSQL can slot in later |
| 2 | Interface | Local web UI — ASP.NET Core API (.NET 10) + React 19 + TypeScript + Vite |
| 3 | Sync execution | Always emit a reviewable `.sql` script; apply is opt-in, transactional, blocked on read-only servers |
| 4 | UI language | English |
| 5 | Grid/diagram libs | TanStack Table + TanStack Virtual; React Flow + dagre for the FK map |

Angular was considered and rejected: the hard UI requirement is virtualized diff grids, TanStack is
React-first, and commercial component libraries are not an option for a personal tool.

## Naming constraint — do not violate this

Every example in `docs/` uses **generic stand-in names**, and that is intentional:

- `DBSERVER-PROD` / `DBSERVER-UAT` / `DBSERVER-DEV` for servers
- `AppProd` / `AppUat` / `AppDev` for databases
- `sales.vCompanySegment`, `dbo.Category`, `Segment` for schema objects

An earlier draft used real internal server hostnames and database names because it made the mockup concrete
during review. Those were scrubbed before publishing — production database hostnames and
per-environment database names in one public-facing file is infrastructure topology, not documentation.

**Never reintroduce real server names, database names, or internal object names here** — not in
prose, sample SQL, screenshots, or test fixtures. The docs now *look* generic, so this constraint is
invisible from reading them, which is exactly why it is written down.

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
tests/DbDelta.Core.Tests/  diff engine unit tests, no database needed
```

`Core` never references a provider package. The API is the only thing that touches a database — the SPA
holds no credentials and issues no SQL.

## The four ideas worth understanding before touching the design

These are covered fully in `docs/PLAN.md`; they are listed here so their existence is not missed.

1. **Data compare is two passes.** Stream `(key, row_hash)` from both sides and merge-join for exact counts
   in O(1) memory; fetch full rows only on demand. Pulling both tables into memory does not survive a
   real table.
2. **The sync plan is a cart of intents, never SQL fragments.** Scope lattice `Database ⊃ Table ⊃ Row`, with
   dedup by change-unit identity. That is what makes overlapping selections structurally unable to emit a
   statement twice. Exclusions are first-class negative entries that survive scope escalation.
3. **Limited row modes must suppress deletes.** Under `Top N` or `Filter`, a row absent from the source only
   means "outside the limit" — honouring deletes would wipe the target while "seeding" it. Correctness rule,
   not a preference.
4. **`CREATE VIEW`/`PROCEDURE`/`FUNCTION`/`TRIGGER`/`SCHEMA` must each begin a T-SQL batch.** The script is
   deliberately one transaction with no `GO`, so these need `EXEC sp_executesql` wrapping. Also
   `DBCC CHECKIDENT … RESEED` after seeding any identity table.

## Build order

1. Solution skeleton + Core model & diff engine + unit tests (no DB)
2. SQL Server schema reader + T-SQL emitter
3. API endpoints + React shell + screens 1–3 (schema path end to end)
4. Volume readout (`sys.dm_db_partition_stats`) wired into connect + table list
5. Data compare engine, per-table modes, parent closure + screens 4–5
6. Apply path + safety guards + screen 6
7. FK map (screen 7)
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

## Environment notes

- Requires the .NET 10 SDK and Node 20+. `sqlcmd`/`bcp` (ODBC 17) are useful for verification.
- `sqlpackage` is not assumed present.
- **Integration tests run against SQL Server LocalDB**, never a shared server. Connection:
  `Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true`.
  `tests/DbDelta.SqlServer.Tests` creates `DbDelta_IntegrationTest_Src` / `_Tgt` per run and drops them
  after. If LocalDB is missing the tests **skip** rather than fail, so `dotnet test` stays green on a
  machine without SQL Server — check for skips before believing a clean run proves the reader works.
  Start it with `SqlLocalDB start MSSQLLocalDB` if a test reports it unavailable.
- LocalDB caps a database at 10 GB and accepts local connections only, so volume behaviour at real
  scale cannot be proven here — only that the queries are correct.
- Connection profiles belong in `%APPDATA%\DbDelta\profiles.json`, never in the repo. `.gitignore` already
  excludes `profiles.json`, `appsettings.Local.json`, `runs/` and `*.sync.sql`.
- Never point a write operation at a production server. `Safety:ReadOnlyServers` in config exists for this
  and its apply path is hard-blocked, not merely warned.
