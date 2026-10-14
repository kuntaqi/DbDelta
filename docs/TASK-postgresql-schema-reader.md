# PostgreSQL schema reader — the stub's first real member

Status: **first increment done and verified** (2026-10-14), on branch `feature/postgresql-schema-reader`.
Follows `docs/TASK-postgresql-provider-stub.md` (the compile-proof stub, merged in #3). `PostgreSqlSchemaReader`
is now a working `ISchemaReader` that reads a live PostgreSQL database into a `DatabaseSchema` for the
structural core (tables, columns, keys, constraints, indexes). It pulled in the first real dependency (Npgsql
10.0.3) and the first real verification. Verified against **PostgreSQL 16.4** installed on this machine
(portable binaries, local-only, trust auth) — `tests/DbDelta.PostgreSql.Tests` runs 27 tests, the integration
one against the live server, all green; the suite skips the integration test when `DBDELTA_PG_TEST` is unset.
What remains is in *Gaps to full parity* below.

## The hard constraint, stated first

**There is no PostgreSQL on this machine, and none can be made to run here** — no server, no `psql`, no Docker
daemon (Hyper-V guest, no nested virtualisation), nothing on 5432. So a reader that *reads a database* cannot
be integration-tested locally at all. This is the same wall the Docker path hit, and it is the dominant
decision for this task — see *Verification*.

## Goal and scope

A `DatabaseSchema` that a PostgreSQL-vs-PostgreSQL comparison can trust. Compare is per-engine (locked
decision: no cross-engine sync), so the only correctness bar is that **two reads by this same reader are
internally consistent** — not that a PG type string equals a SQL Server one.

Proposed **first increment** (the structural heart of schema compare):

- database name + collation (`current_database()`, `pg_database.datcollate`)
- tables, columns (type, nullability, ordinal, per-column collation, identity, generated/computed, default)
- primary keys, unique constraints, foreign keys (with on-delete/on-update actions), indexes, check constraints

Deliberately **deferred to a second increment**, and surfaced through `DatabaseSchema.ReadWarnings` so an
incomplete read never reads as complete: views, routines/functions, triggers, sequences, user-defined types
(domains/composite/enum). Leaving these empty is *safe* for PG-vs-PG compare — both sides are blinded equally,
so no spurious "drop all views" — but it is incomplete, which is exactly what `ReadWarnings` exists to say.

## Cross-engine landmines (from mapping the model + comparer; budget work here, not in guessing)

The model is already engine-neutral in *shape*; the SQL-Server assumptions are concentrated and known:

1. **Type name casing feeds an ordinal compare.** The comparer reduces a column type to `DataTypeSpec.ToString()`
   and compares it with `StringComparison.Ordinal`, even though `DataTypeSpec.Equals` is case-insensitive.
   So the reader must pick **one canonical casing** for PG type names and never waver. Decision: **lowercase**
   (PG's own convention — `integer`, `character varying`, `timestamp with time zone`). Fill `MaxLength` for
   `varchar`/`char`, `Precision`/`Scale` for `numeric`, `Precision` for the fractional-seconds digits of
   `time`/`timestamp` (the convention `NarrowingAnalyzer` relies on), `IsUserDefined`+`Schema` for domains.
2. **Bare, unquoted, lowercase identifiers.** Names in the model are unquoted (quoting is an emit-time concern).
   PG catalogs already hand back lowercased unquoted names for unquoted identifiers, which matches. Mixed-case
   *quoted* PG identifiers are a hazard because `ObjectIdentity` pairs by `OrdinalIgnoreCase` — note it, do not
   solve it now; typical lowercase schemas are fine.
3. **IndexKind has no PG access methods.** The enum is `Rowstore/Columnstore/Xml/Spatial/Hash`. First increment
   maps every PG index to `Rowstore` (accepting loss of btree/gin/gist/brin fidelity) rather than editing the
   Core enum. Flagged as follow-up; the enum's own comment already anticipates PG values.
4. **Clustered is SQL-centric.** PG has no clustered index. Set `IsClustered = false` everywhere — and mind
   that `PrimaryKeyDefinition.IsClustered` defaults to **true**, so it must be set explicitly or every PK diffs.
5. **FillFactor is compared.** The SQL reader always sets `Extras["FillFactor"]`. Set it consistently from
   `pg_class.reloptions` (default 100), or rely on `ComparisonOptions.IgnoreFillFactor`. Pick one; don't leave
   it half-set.
6. **NarrowingAnalyzer and SqlBodyNormalizer are T-SQL-specific**, but neither is on the reader's path:
   narrowing is an emit/apply concern, and body normalisation only matters once routine/view/trigger bodies are
   read (second increment). Both are follow-ups, named here so they are not forgotten when bodies arrive —
   `SqlBodyNormalizer` does not understand PG dollar-quoting (`$$...$$`).

Not compared, safe to leave empty: `Statistics`, `SchemaBoundReferences`, `ComputedFrom`,
`CheckConstraint.Columns`, most `ProviderExtras`. `DependsOn` affects only emission order.

## Design

- Add **Npgsql** to `src/DbDelta.PostgreSql/` (reverses the stub's "no package" — a reader that reads needs a
  client). `PostgreSqlSchemaReader : ISchemaReader` mirrors `SqlServerSchemaReader`'s fold pattern: flat
  catalog queries ordered by (schema, name, ordinal), folded into a per-table accumulator.
- Catalog SQL centralised in a `PgCatalogQueries` const-string class, same as `CatalogQueries.cs`.
- Permission/again-missing handling keyed on PG SqlState (`42501` insufficient privilege, `42P01` undefined
  table) → `ReadWarnings`, the way the SQL reader keys on 229/208.
- **Extract the pure, dialect-mapping bits into testable static methods** — PG-type-row → `DataTypeSpec`,
  referential-action char → `ReferentialAction`, index access method → `IndexKind`, canonical casing. These are
  unit-testable with no database and are where the casing/length conventions live.
- `schema` scope: read `information_schema`/`pg_catalog` for non-system schemas only (exclude `pg_catalog`,
  `information_schema`, `pg_toast`).

## Verification (the decision this task turns on)

Local verification can only reach: the build (Npgsql compiles in, warnings-as-errors), and **unit tests on the
pure mappers** (type, action, index-kind, casing). That proves the conversions, not that a read works.

The honest full-verification options:

- **A — CI-verified (recommended).** Write the reader + integration tests that connect via a connection string
  from an env var and **skip when it is absent** (exactly like the LocalDB tests skip). Add a `postgres`
  service container to the **Linux** job in `.github/workflows/build.yml` and set that env var to it, so the
  tests *actually run and verify the reader* on every push and PR. Linux runners have Docker; this is the one
  place real PG verification is available to this project. Mirror-image of today's setup: LocalDB tests run on
  Windows CI and skip on Linux; PG tests run on Linux CI and skip on Windows and locally.
- **B — Mapping-only + labelled unverified.** Unit-test the mappers, ship the reader labelled "written, not yet
  run against a live engine" like the Docker path. No CI change. Weaker: the fold/assembly and the catalog SQL
  never meet a real server.
- **C — A live endpoint now.** The user supplies a reachable PostgreSQL connection string for this session.
  Then it can be verified here and in CI both.

## Build order

1. Npgsql reference + pure mapper statics + their unit tests (no DB) + `PgCatalogQueries` skeleton.
2. `PostgreSqlSchemaReader.ReadAsync` for the first-increment objects, folding catalog rows.
3. Integration tests (skip-when-absent) + the chosen verification wiring.
4. Second increment (views/routines/triggers/sequences/UDTs) + the body-normaliser and narrowing follow-ups —
   separate task.

## Gaps to full parity (what the first increment does not do yet)

Tracked here so the path from this increment to a complete PostgreSQL schema reader is explicit. None of these
breaks a PostgreSQL-vs-PostgreSQL compare — both sides are read by the same reader, so a gap blinds both
equally — but each is a thing a sync would not reproduce.

**Object kinds not read at all** (surfaced at runtime through `DatabaseSchema.ReadWarnings`):

- **Views** — definition, columns, dependency order.
- **Routines** — functions and procedures. Reading their bodies then needs `SqlBodyNormalizer` to understand PG
  dollar-quoting (`$$...$$`) and `E'...'` escapes, or function-body diffs will be false positives.
- **Triggers**.
- **Sequences** as standalone objects (identity-owned sequences are already read via the column's identity).
- **User-defined types** — domains (≈ `Alias`), composite types, and enums (no exact `UserDefinedTypeKind`
  slot — map to `Alias` or extend the enum).

**Within the objects that are read:**

- **Expression indexes and partial-expression columns.** A key at attnum 0 is rendered from
  `pg_get_indexdef`; this is captured but untested, and complex expressions may format differently between
  reads. Validate before relying on expression-index compare.
- **Exclusion constraints** (`EXCLUDE USING …`) are neither modelled nor read; their backing indexes are
  filtered out, so they vanish silently. Needs a model decision.
- **`INCLUDE` columns** on indexes are read but untested (the fixture has none yet).
- **Index access-method fidelity.** `gin`/`gist`/`brin`/`spgist` all collapse to `IndexKind.Rowstore`; only
  `hash` maps distinctly. Extending `IndexKind` with PG methods (the enum's own comment anticipates this) is the
  fix.
- **`RESTRICT` referential action** collapses to `NoAction` (no model counterpart).
- **Partitioning**, **storage/`reloptions`** beyond `fillfactor`, **comments**, **column `STORAGE`/compression**,
  and **generated-column virtual vs stored** nuance are not represented.

**Cross-engine follow-ups outside the reader** (needed before PG *sync*, not PG *read*):

- `NarrowingAnalyzer` has only T-SQL type ladders, so PG narrowing detection silently does nothing — it
  falls through to the generic `Converts` verdict. Needs PG type ladders (`int8`/`int4`/`int2`, `float8`/
  `float4`, etc.).
- Mixed-case *quoted* PG identifiers collide under the comparer's `OrdinalIgnoreCase` pairing. Typical
  lowercase schemas are safe; this is a latent hazard, not a current bug.

## Next increments (build order from here)

1. Views + routines (with `SqlBodyNormalizer` taught PG quoting) + triggers.
2. Sequences + user-defined types (domains/composite/enums).
3. The within-object gaps above, each with a fixture case.
4. The cross-engine follow-ups — only once a PG *emitter* exists and sync is on the table.

## Workflow note

As with the stub, steps 4–6 of the standard workflow (before-screenshots, mockup, tester `.docx`) do not apply:
no screen changes, no tester-visible behaviour (the API still wires only SQL Server). Backend + tests + CI.
