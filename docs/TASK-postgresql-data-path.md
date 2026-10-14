# PostgreSQL data path — the compare read half

Status: **increment 1 (data-compare read path) done and verified** (2026-10-14). Follows the schema reader
(`docs/TASK-postgresql-schema-reader.md`) and the schema write path (`docs/TASK-postgresql-write-path.md`).

## The data path is two halves

Mapping the SQL Server data path showed it splits cleanly, and each half is its own increment:

1. **Data-compare read path** — this increment. The readers that let a PostgreSQL table be compared and its rows
   fetched: row hash (the merge-join feed), row detail, row-by-value (parent closure), fingerprint, key
   uniqueness, and volume.
2. **Data-sync write path** — the data-script emitter, a `COPY`-based staged writer, and the executor's `COPY`
   branch for the bulk-load steps. Deferred to the next increment.

This increment is #1, chosen because it is the foundation — the write half's tests need these readers to set up
data to sync — and it is independently verifiable: PostgreSQL data comparison and parent closure work end to end.

## What was implemented

All against Npgsql, mirroring the SQL Server classes the Explore mapped:

- **`PgRowDigest`** — the server-side digest everything turns on. Each column becomes `|<len>:<text>`
  (length-prefixed so `('a','bc')` and `('ab','c')` cannot collapse, NULL-tagged so NULL is distinct from empty),
  rendered with `::text` and hashed with `md5`. Two decisions differ from SQL Server and are deliberate: `md5`
  rather than SHA-256 via pgcrypto (built in, no extension; this is change detection, not cryptography), and a
  single `::text` rendering for every type rather than per-type `CONVERT` styles — both sides are PostgreSQL read
  by this same code, so the digest only has to be internally deterministic, not match another engine.
- **`PostgreSqlRowHashReader`** — streams `(key, hash)` ordered by the key under `COLLATE "C"` (PostgreSQL's
  byte-ordinal collation), because the client merge join compares with `StringComparer.Ordinal` and only a
  byte-order sort agrees; the default collation would desynchronise the merge. Top-N applies to the source only
  (`LIMIT`, with an inner `ORDER BY` for a reproducible window); a filter predicate is injected verbatim.
- **`PostgreSqlRowDetailReader`** — fetches full rows for a batch of canonical keys through a `VALUES`-constructor
  join on the same digest expression (not an `IN` list — the join evaluates the digest once per row). Values come
  back as `::text`.
- **`PostgreSqlRowByValueReader`** — addresses parent rows by literal column values (an `IN` list for a
  single-column key, OR-ed tuples for a composite one), using typed literals (`'v'::type`) so a numeric or binary
  key is addressed as its own type. A key with a NULL cannot be addressed by equality and is left out.
- **`PostgreSqlFingerprintReader`** — the cheap "does this table differ" screen. PostgreSQL has no
  `BINARY_CHECKSUM`/`CHECKSUM_AGG`, so a per-row `hashtext()` is aggregated three independent ways — `count`,
  `bit_xor` and `sum` — so an accidental collision across all three is vanishingly unlikely. Tables batch as
  separate statements so one failure takes only its own result set down.
- **`PostgreSqlKeyUniquenessChecker`** — `ProfileAsync` measures every candidate column's distinct and null
  counts in one scan (skipping `json`/`xml`, which have no equality operator); `CheckAsync` validates a chosen key
  through the same digest the compare uses.
- **`PostgreSqlVolumeReader`** — table footprints from `pg_class.reltuples` (clamped; -1 until analysed) and
  `pg_relation_size`/`pg_indexes_size`; the database size from `pg_database_size`. PostgreSQL has no per-database
  log file (WAL is cluster-wide), so `LogBytes` is 0.
- **`PgLiteral`** — a value rendered as text becomes a typed literal by quoting and casting back (`'v'::type`),
  one shape for every type.

## Verification

Unit-tested without a database: `PgRowDigest` and `PgLiteral` (the SQL-text shape the readers rest on).

End to end against **PostgreSQL 16.4** (`PostgreSqlDataComparePathTests`): two schemas hold a `widget` table that
differs by one insert, one update and one delete. The two hash streams are merged through the real Core
`DataComparer` and the classification checked (1/1/1 + 1 same); then the row-detail, row-by-value, uniqueness,
fingerprint and volume readers are exercised over the same data. Full suite green: Core 282, Api 98, SqlServer
163, PostgreSql 66.

## Deferred

- **Data-sync write path** (the next increment): `PostgreSqlDataScriptEmitter` (inline literals via `PgLiteral`
  and a `COPY`-based staged writer past a size threshold), the executor's `COPY FROM STDIN` branch for
  `BulkLoad` steps, and identity handling (`OVERRIDING SYSTEM VALUE` / `setval` reseed).
- **Collation fact reader** — still stubbed. `CollationFact` is SQL-Server-shaped (code page + a comparison-style
  bitmask); resolving PostgreSQL collation sensitivity faithfully (ICU locale attributes / `collisdeterministic`)
  is a modelling task, not a quick port, and the collation precondition is not reachable until the API selects
  the PostgreSQL provider.
- **API provider selection** — the remaining cross-cutting increment that makes any of this reachable from the UI.

## Workflow note

As with the other provider increments, steps 4–6 of the standard workflow (before-screenshots, mockup, tester
`.docx`) do not apply: no screen changes, no tester-visible behaviour (the API still wires only SQL Server).
Backend + tests + CI.
