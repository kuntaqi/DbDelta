# API provider selection — making PostgreSQL reachable

Status: **backend done and verified** (2026-10-14). The connect-screen engine selector (frontend) is the
follow-up increment and is scoped at the end. Follows the schema reader, schema write path and data path
(`docs/TASK-postgresql-*.md`), which built a complete PostgreSQL provider the API never referenced.

## The split, and why this is the backend half

Mapping the connect flow found the engine was already abstracted behind `IDatabaseProvider` and a full
PostgreSQL provider existed — the API just bound `SqlServerProvider` and `SqlServerScriptExecutor` as global
singletons and never referenced the PostgreSQL project. So the increment has two halves:

- **Backend (this increment):** select the provider per connection, generalise the SQL-Server-coupled pieces,
  and finish the provider's server-level surface. No visible change — the engine defaults to SQL Server, so an
  older client behaves exactly as before; PostgreSQL becomes reachable through the API.
- **Frontend (next increment):** an engine selector on the connect screen. That changes a screen, so it follows
  the full UI workflow (before-screenshots → mockup sign-off → tester docx → code) rather than being coded
  blind. The backend half is a pure refactor and needs none of that, which is why they are split.

## What changed

- **`IDatabaseProvider.CreateScriptExecutor()`** — the executor now belongs to the provider, like the emitter.
  This removes the asymmetry where the executor was a separate global singleton that could disagree with the
  provider about which engine. Both providers implement it.
- **`IDatabaseProviderFactory` / `DatabaseProviderFactory`** (API) — indexes every registered provider by its
  `Key` (dormant until now) and resolves one per request. `DatabaseEngine.Normalize` maps a request's engine to
  a key, defaulting null to SQL Server and accepting a few aliases.
- **`ConnectionRequest.Engine`** and **`CompareSession.Engine`** — the engine rides on the request, and the
  session remembers it so every body-less `GET /compare/{id}/*` follow-up resolves the right provider.
- **Services** (`CompareService`, `DataCompareService`, `ApplyService`, `InstanceService`) now take the factory
  and resolve the provider from the request's or session's engine at method time, rather than being handed one
  global provider. `CompareAsync` requires both sides to name the same engine — compare is per-engine, a locked
  non-goal to cross engines.
- **`ConnectionFactory`** is engine-aware: SQL Server through `SqlConnectionStringBuilder` (named instances,
  integrated security, `HOST,PORT`), PostgreSQL through `NpgsqlConnectionStringBuilder` (host/port/database/
  username/password, no integrated security). `Retarget` (repointing at another database for an instance survey)
  branches the same way.
- **Provider-neutral errors.** Both engines fail through `System.Data.Common.DbException`, so the six endpoint
  catch sites take that, and `ConnectionProblem.Describe` branches on the exception type — SQL Server integer
  error numbers, PostgreSQL five-character SQLSTATEs, a pure connection failure with neither. `IsMissingDatabase`
  replaces the hard-coded `4060` so the compare endpoints still name which side failed.
- **`TSqlEmitterAdapter` → `ScriptEmitterAdapter`.** It was already engine-neutral (it just calls
  `provider.CreateScriptEmitter()`); the T-SQL name was now misleading.
- **The PostgreSQL provider is finished.** The three server-level methods — `ProbeAsync`, `ListDatabasesAsync`,
  `DescribeDatabaseAsync` — were still stubbed, so `/probe` (Test Connection) and `/instance` (survey) would have
  500'd for PostgreSQL. They are now catalog queries (`current_setting('server_version')`, `pg_database`,
  per-schema object counts). The collation fact reader, previously stubbed, now returns each name Unresolved —
  `CollationFact` is SQL-Server-shaped and cannot express PostgreSQL collation sensitivity, so the precondition
  treats it as "unknown" rather than claiming a sameness it has not checked. `StubGap` is gone: nothing stubs.
- **A pinned dependency.** Npgsql pulls `System.Security.Cryptography.Pkcs 9.0.13` transitively, which has a
  published advisory, so the API fails its audit-as-error gate. Pinned to the patched `10.0.13` rather than
  suppressing the audit, so the gate stays on for everything else.

## Verification

Without a database: `DatabaseProviderFactory`/`DatabaseEngine` (a null engine resolves to SQL Server, aliases
resolve, an unknown engine is rejected, each provider hands back its own executor), and `ConnectionFactory`
builds and reparses a PostgreSQL connection string and retargets it.

Against **PostgreSQL 16.4**, through the real app (`WebApplicationFactory<Program>`): `POST /api/probe` with
`engine=postgresql` returns the database and "PostgreSQL"; `POST /api/compare` against the same database returns
200; `POST /api/probe` at a database that does not exist returns a clean 400, not a 500 — the provider-neutral
error mapper turning SQLSTATE `3D000` into a message. Full suite green: Core 282, SqlServer 163, Api 118,
PostgreSql 70.

## Deferred — the connect-screen engine selector (next increment)

The frontend still sends no engine, so it always talks to SQL Server. The follow-up adds an engine chip toggle
to the connect form (modelled on the auth toggle), gates Windows auth when PostgreSQL is selected (PostgreSQL has
no integrated security), and carries `engine` on the saved profile and the recent-pair record. Because it
changes the connect screen, it follows the full workflow: capture the current screen, a `design-implementer`
mockup, sign-off, a `docx-author` tester doc, then code.

A modelling follow-up unrelated to the UI: real PostgreSQL collation sensitivity (ICU locale attributes /
`collisdeterministic`) so the collation precondition can judge a PostgreSQL pair instead of reading "unknown".

## Workflow note

This backend half changes no screen and has no tester-visible behaviour (the frontend still wires only SQL
Server), so steps 4–6 of the standard workflow do not apply to it — they apply to the frontend increment that
follows. Backend + tests + CI.
