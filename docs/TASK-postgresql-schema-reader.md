# PostgreSQL schema reader — the stub's first real member

Status: **increments 1–4 done and verified** (2026-10-14). Follows `docs/TASK-postgresql-provider-stub.md`
(the compile-proof stub, merged in #3). `PostgreSqlSchemaReader` is a working `ISchemaReader` that reads a live
PostgreSQL database into a `DatabaseSchema` for the structural core (tables, columns, keys, constraints,
indexes — merged in #6), **views, routines and triggers** (increment 2), and now **sequences and user-defined
types** (increment 3). It pulled in the first real dependency (Npgsql 10.0.3) and the first real verification.
Verified against **PostgreSQL 16.4** installed on this machine (portable binaries, local-only, trust auth) —
`tests/DbDelta.PostgreSql.Tests` runs against the live server, the integration test among them, all green; the
suite skips the integration test when `DBDELTA_PG_TEST` is unset. What remains is in *Gaps to full parity* below.

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

**Object kinds not read at all** (surfaced at runtime through `DatabaseSchema.ReadWarnings` when present):

- **Range and multirange types** (`typtype 'r'`/`'m'`) — no model slot and uncommon; a `ReadWarning` names any
  that exist rather than dropping them silently. Everything else a `CREATE TYPE` can make is read (increment 3).
- **Exclusion constraints** (`EXCLUDE USING …`) are not modelled; their backing indexes are filtered out. As of
  increment 4 a `ReadWarning` names any that exist rather than letting them vanish silently — full modelling
  still needs a model decision.
- **Aggregate and window functions** (`prokind 'a'`/`'w'`) are skipped; `pg_get_functiondef` cannot script them,
  so reading them means synthesising the `CREATE` from `pg_aggregate` — deferred.

Read as of increment 3, with these smaller gaps inside them:

- **Domains** map to `UserDefinedTypeKind.Alias` with base type, nullability and — as of increment 4 — their
  `CHECK` constraints (so a domain differing only in a check now diffs). A domain's `DEFAULT` is still not read
  (no model field for it).

Read as of increment 2, with these smaller gaps inside them:

- **Views**: materialised views (`relkind = 'm'`) are read as of increment 4, flagged in `Extras`. A view's
  `Definition` is still the `SELECT` body (`pg_get_viewdef`), not a full `CREATE`, unlike routines/triggers.
- **Routines**: `DependsOn` is left empty (no emitter consumes it yet).
- **Routine/view bodies** still need `SqlBodyNormalizer` to understand PG dollar-quoting (`$$...$$`) and
  `E'...'` escapes before they can be compared without false diffs — a compare-side follow-up with no consumer
  until PG sync is wired.

**Within the objects that are read:**

- **`RESTRICT` referential action** collapses to `NoAction` (no model counterpart).
- **Partitioning**, **storage/`reloptions`** beyond `fillfactor`, **comments**, **column `STORAGE`/compression**,
  and **generated-column virtual vs stored** nuance are not represented.
- **Index access-method fidelity, `INCLUDE` columns and expression indexes** are handled as of increment 4:
  `gin`/`gist`/`brin`/`spgist` now map to distinct `IndexKind` values, and the `INCLUDE`/expression paths have
  fixture coverage. Complex expression-index formatting across two databases is validated for the simple case;
  exotic expressions may still format differently between reads.

**Cross-engine follow-ups outside the reader** (needed before PG *sync*, not PG *read*):

- `NarrowingAnalyzer` has only T-SQL type ladders, so PG narrowing detection silently does nothing — it
  falls through to the generic `Converts` verdict. Needs PG type ladders (`int8`/`int4`/`int2`, `float8`/
  `float4`, etc.).
- Mixed-case *quoted* PG identifiers collide under the comparer's `OrdinalIgnoreCase` pairing. Typical
  lowercase schemas are safe; this is a latent hazard, not a current bug.

## Next increments (build order from here)

1. **Done — views + routines + triggers.** See *Increment 2* below.
2. **Done — sequences + user-defined types (domains/composite/enums).** See *Increment 3* below.
3. **Done — the high-value within-object gaps** (index access-method fidelity, materialised views, domain
   checks, exclusion-constraint warning, and fixtures for the untested `INCLUDE`/expression-index paths). See
   *Increment 4* below. The remaining within-object gaps stay documented above, each with its reason.
4. The cross-engine follow-ups — only once a PG *emitter* exists and sync is on the table.

## Increment 2 — views, routines, triggers (design)

Fills `DatabaseSchema.Views`, `.Routines` and `.Triggers`, which the first increment left empty behind a
`ReadWarning`. Still reader-only; still no PG compare path wired, so the compare-side follow-ups below have no
live consumer yet and stay deferred.

Catalog sources (deterministic, so two reads of the same object produce identical text):

- **Views** — `pg_class` where `relkind = 'v'`, definition from `pg_get_viewdef(oid, true)`. This is the
  `SELECT` body, not a full `CREATE` — a documented inconsistency with routines/triggers below, cheap to
  revisit when an emitter needs a full statement. Materialised views (`relkind = 'm'`) are a gap.
- **Routines** — `pg_proc` where `prokind in ('f','p')`, definition from `pg_get_functiondef(oid)` (a full
  `CREATE OR REPLACE`). Aggregates and window functions (`'a'`,`'w'`) are a gap.
- **Triggers** — `pg_trigger` where `not tgisinternal` (excludes FK/constraint triggers), definition from
  `pg_get_triggerdef(oid, true)`. `IsDisabled` from `tgenabled = 'D'`.

Two PG-specific identity choices, because the model's `(schema, name)` key is unique in SQL Server but not in
PostgreSQL:

- **Triggers** are named per *table*, not per schema (`set_updated_at` on many tables is normal), so a bare
  `(schema, trigger)` key would collide and silently drop triggers. Identity name is `"{table}.{trigger}"`.
- **Routines** can be overloaded (`foo(int)` vs `foo(text)`), so the identity name is
  `"{name}({identity-args})"` via `pg_get_function_identity_arguments` — how PostgreSQL itself disambiguates.

`RoutineKind`: `'p'` → `Procedure`; `'f'` with `proretset` → `TableValuedFunction`; otherwise `ScalarFunction`.
`DependsOn` is left empty — it drives emission order, which has no consumer until an emitter exists.

Compare-side follow-ups (deferred, no consumer yet; needed before PG *sync*): `SqlBodyNormalizer` does not
understand dollar-quoting (`$$...$$`) or `E'...'`, and `ProgrammableHeaderReader` parses T-SQL `CREATE`
headers, not PG ones. Both only matter once two PostgreSQL schemas are actually compared.

## Increment 3 — sequences, user-defined types (design)

Fills `DatabaseSchema.Sequences` and `.UserDefinedTypes`, the last two object kinds the earlier increments left
empty behind the standing `ReadWarning`. That blanket warning is removed; a narrower one fires only for the
range/multirange types the model cannot hold. Still reader-only — no PG compare path is wired, so the
compare-side follow-ups stay deferred.

**One Core change, decided deliberately.** PostgreSQL enums have no slot in `UserDefinedTypeKind` (`Alias`,
`Table`, `Clr`) and no field anywhere to hold their labels, so mapping an enum onto an existing kind would make
the comparer — which weighs `Kind`/`BaseType`/`Nullable`/`Columns` — blind to label changes and silently report
two different enums as identical, the exact "incomplete read looks complete" failure the project forbids. So
Core grows a value and a field: `UserDefinedTypeKind.Enum`, and `UserDefinedTypeDefinition.EnumLabels` (ordered;
PostgreSQL enum order is significant). `SchemaComparer.CompareUserDefinedTypes` compares the labels. This is
safe across engines: the SQL Server reader never produces `Enum`, so a PG enum never reaches the T-SQL emitter,
and the emitter's `== Clr` / `== Table` checks fall through for unknown kinds anyway — no exhaustive switch.

Catalog sources:

- **Sequences** — `pg_sequence` joined to `pg_class` (`relkind = 'S'`), excluding sequences with an internal
  dependency (`pg_depend.deptype = 'i'`) on a column, which are the identity sequences already read through the
  column's `IdentitySpec`. Fields: `format_type(seqtypid)` → `DataType`, `seqstart`, `seqincrement`, `seqmin`,
  `seqmax`, `seqcycle`.
- **User-defined types** — `pg_type` where `typtype in ('d','e','c')` in non-system schemas; a composite type's
  backing relation must be `relkind = 'c'` so the per-table/per-view row types are excluded. One query returns
  the type row (kind, domain base type via `typbasetype`, domain nullability via `not typnotnull`); composite
  columns and enum labels are folded in from two more queries keyed by type identity, the way index columns are
  folded. `typtype 'r'`/`'m'` rows are turned into a `ReadWarning`, not a UDT.

Kind mapping lives in `PgTypeMapper.MapUserDefinedTypeKind` (`'d'`→`Alias`, `'e'`→`Enum`, `'c'`→`Table`),
unit-tested without a database alongside the other dialect maps.

Compare-side follow-ups are unchanged from increment 2 and still have no consumer until a PG emitter exists.

## Increment 4 — the high-value within-object gaps (design)

Closes the within-object gaps that are pure reader work and testable today, and documents why the rest wait.
Still reader-only.

- **Index access-method fidelity.** `IndexKind` grows `Gin`, `Gist`, `Brin` and `Spgist` (additive; the enum's
  own comment anticipated PG values). `PgTypeMapper.MapIndexKind` maps each access method distinctly, so two
  indexes that differ only by method now diff instead of both reading as `Rowstore`. Safe across engines: the
  SQL Server reader never produces these, and `IndexEmitSupport`'s switch has a `default`, so an unknown kind
  falls through rather than throwing — a PG kind never reaches the T-SQL emitter anyway.
- **Materialised views.** The `Views` query reads `relkind in ('v','m')`; a matview carries `Extras["Materialized"]
  = "true"`. `pg_get_viewdef` returns its `SELECT` just as for a plain view. A matview and a plain view cannot
  share a name (both are `pg_class` relations), so there is no identity collision.
- **Domain CHECK constraints.** A domain's checks live in `pg_constraint` keyed by `contypid` (the type oid), not
  `conrelid`, and `pg_get_expr(conbin, 0)` renders them (relation oid 0, because a domain check references
  `VALUE`, not a column). They fold into the UDT's `CheckConstraints`, which `SchemaComparer.Shape` already
  folds into the compared string — so the gap closes with no comparer change.
- **Exclusion constraints.** Still not modelled, but a query over `pg_constraint` where `contype = 'x'` turns any
  that exist into a `ReadWarning` naming them, so they no longer vanish silently. Full modelling needs a model
  decision and waits.
- **Untested paths, now covered.** The fixture gains an `INCLUDE` index, an expression index (`lower(name)`), a
  `gin` index on a `text[]` column, a materialised view, a domain with a check, and a table with an exclusion
  constraint — so every path above meets a live assertion.

Deferred, each with its reason, in *Gaps to full parity*: aggregate/window functions, `RESTRICT`, domain
`DEFAULT`, full exclusion modelling, partitioning/storage/comments, and the cross-engine follow-ups.

## Workflow note

As with the stub, steps 4–6 of the standard workflow (before-screenshots, mockup, tester `.docx`) do not apply:
no screen changes, no tester-visible behaviour (the API still wires only SQL Server). Backend + tests + CI.
