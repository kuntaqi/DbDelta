# PostgreSQL write path — the schema emitter and executor

Status: **increment 1 (schema DDL) done and verified** (2026-10-14). Follows the schema reader
(`docs/TASK-postgresql-schema-reader.md`, increments 1–4, all merged). This is the first half of the write
path: turning a schema diff into PostgreSQL DDL and applying it. The data path and the API's runtime provider
selection are the two remaining halves, scoped at the end.

## What "the write path" is, and why it is three increments

The Explore of the provider surface found the whole write path **and** the whole data-compare read path stubbed
— only the quoter and the schema reader were real. "The write path" is therefore three independent chunks, each
its own increment:

1. **Schema DDL emitter + executor** — this increment.
2. **Data path** — the stubbed data-compare readers (row hash/detail/by-value, fingerprint, key-uniqueness,
   collation, volume) plus the data-script emitter and a `COPY`-based staged writer.
3. **API provider selection** — the API hard-wires `SqlServerProvider` and `SqlServerScriptExecutor` in
   `Program.cs`, `ConnectionFactory` is `SqlClient`-bound, and error handling catches `SqlException`. None of
   this reaches PostgreSQL yet, so even the finished read/compare/emit is not visible in the app.

This increment is #1, chosen because it is the core of "write" and is verifiable end to end against the local
PostgreSQL 16.4 without touching the API.

## Scope of this increment

Implemented: `PostgreSqlScriptEmitter` (`IScriptEmitter`) and `PostgreSqlScriptExecutor` (`IScriptExecutor`),
plus `PgStatementWriter` (statement text) and `PgTypeText` (type rendering). The emitter mirrors `TSqlEmitter`'s
phase model — `ScriptPhase`, `SyncScript` and `ScriptStep` are engine-neutral and reused unchanged — and emits:

- **Schemas** — `CREATE SCHEMA IF NOT EXISTS` for the schemas of source-only objects (not `public`).
- **Tables** — `CREATE TABLE` (columns with type, collation, identity, generated-stored, default, nullability);
  `DROP TABLE` for target-only, inbound foreign keys dropped first.
- **Columns** — `ADD`/`DROP COLUMN`, and `ALTER COLUMN` split into the independent PostgreSQL sub-commands
  (`TYPE`, `SET`/`DROP NOT NULL`, `SET`/`DROP DEFAULT`). This is simpler than the T-SQL path: PostgreSQL applies
  each sub-command independently and sees a new column immediately, so none of T-SQL's batch-recompile
  bracketing is needed.
- **Keys, unique, check, foreign keys** — `ADD`/`DROP CONSTRAINT`, foreign keys dropped in their own phase
  before tables and added after, which is what removes FK cycles as a concern.
- **Indexes** — `CREATE INDEX ... USING <method>` with the access method from `IndexKind`, `INCLUDE`, partial
  `WHERE`, and `DESC` only for btree.
- **Sequences** — `CREATE`/`DROP`/`ALTER SEQUENCE`; a type change is reported, and the current value is never
  touched (same stance as the SQL Server side).
- **Types** — `CREATE DOMAIN`/`CREATE TYPE AS ENUM`/`CREATE TYPE AS (…)`, create and drop only.
- **Programmables** — views (`CREATE OR REPLACE VIEW`; a materialised view is dropped and recreated, as it has
  no replace form), routines (the stored `pg_get_functiondef` text is a full `CREATE OR REPLACE`, reused
  verbatim), and triggers (drop + create).

The executor opens one `NpgsqlConnection` and one transaction — PostgreSQL DDL is transactional, so a failure
anywhere rolls the whole thing back and leaves the target unchanged. Schema DDL carries no `BulkLoad` steps, so
the executor has no `COPY` path yet; a script that carries rows is refused (`Blocked`) rather than half-applied.

### Refused, not emitted wrongly (carried in `SyncScript.Refusals`)

- A column change that would rewrite the table — an identity or generated-column change.
- An `ALTER` to a user-defined type's own shape (a changed domain/enum/composite) — dropping and recreating it
  would take down the columns that use it.
- An expression index — its text cannot be reconstructed from the catalog columns without risking a different
  formatting between reads.

### One engine-neutral Core change

`SyncScript.ToSql()` hard-coded the T-SQL session prologue (`SET XACT_ABORT ON` …), which PostgreSQL rejects.
The three `SET` lines became `SyncScript.SessionSettings`, defaulting to exactly those so SQL Server behaviour is
unchanged; the PostgreSQL emitter sets it empty. The transaction keywords `BEGIN TRANSACTION` / `COMMIT
TRANSACTION` are valid in both engines and stay. The apply path sets session state in the executor, not from
this field, so this only shapes the downloadable text.

## Verification

Unit-tested without a database: `PgTypeText` (type rendering, including temporal precision placement),
`PostgreSqlScriptEmitter` (phases, refusals, PostgreSQL session settings) via the real `SchemaComparer` over
hand-built schemas, and the executor's refusal of a row-carrying script.

End to end against **PostgreSQL 16.4** (`PostgreSqlWritePathTests`): two scratch databases are built to differ
across every object kind the emitter handles — a new table with PK/unique/FK/check/btree+gin indexes, a
generated column, a user-defined-type column, a sequence, an enum, a domain with a check, a composite type, a
view, a materialised view, a function and a trigger; a table that differs (a widened column, a dropped column,
an added column); and a target-only table to drop. The script is emitted, applied with the real executor, and
the target is re-read and compared again — the second comparison is empty, proving convergence. The fixture is
built to need no refusals.

## Deferred (the other two write-path increments)

- **Data path**: the data-compare readers and the data-script emitter, with a `COPY`-based staged writer and the
  executor's `COPY` branch for `BulkLoad` steps.
- **API provider selection**: a per-engine resolver for `IDatabaseProvider` and `IScriptExecutor` (the executor
  is wired apart from the provider, which is the main structural gap), a PostgreSQL path in `ConnectionFactory`,
  and provider-neutral error handling in `Program.cs`.
- **Within the emitter**: inter-programmable ordering relies on `DependsOn`, which the reader leaves empty, so a
  view that references another new view may be emitted before it; a routine whose return type changed would fail
  at apply rather than being refused (the stored definition is reused verbatim); always-vs-by-default identity is
  not distinguished (the reader does not capture it), so a recreated identity column is always `BY DEFAULT`.

## Workflow note

As with the reader increments, steps 4–6 of the standard workflow (before-screenshots, mockup, tester `.docx`)
do not apply: no screen changes, no tester-visible behaviour (the API still wires only SQL Server). Backend +
tests + CI.
