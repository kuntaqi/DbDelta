# PostgreSQL provider stub — proving the abstraction holds

Status: **done and verified** (2026-10-14), on branch `feature/postgresql-provider-stub`. Scope was locked to
**interface-only (compile proof)**. `src/DbDelta.PostgreSql/` now carries a second `IDatabaseProvider`
satisfying the whole provider surface against an unchanged `Core`; `tests/DbDelta.PostgreSql.Tests` (10 tests)
covers it, and the solution builds clean under warnings-as-errors. Decision 1's "provider-abstracted" claim
has now met a second implementation — though not a second *engine*. Was build-order step 8, the last item
`docs/PLAN.md` marked *Not started*.

## Goal

A second `IDatabaseProvider` exists, compiles against `Core` unchanged, and covers every member of the
provider surface. That is the whole deliverable. It does **not** connect to PostgreSQL, add Npgsql, read a
catalog, or emit a line of SQL. Its value is structural: if a second class can satisfy the interfaces
without `Core` bending, the abstraction is real; where it cannot, the friction is the finding.

Out of scope (explicitly): real pg_catalog/information_schema reads, a Postgres DDL emitter, cross-engine
sync (ruled out in PLAN — compare is per-engine), runtime provider selection in the API, and any Docker
work.

## The surface a provider must satisfy

`IDatabaseProvider` (`Core/Providers`) is the single entry point: `Key`, `Quoter`, nine factory methods,
three server-level async methods. The factories hand back these interfaces — each needs a stub type:

| Interface | Namespace | Member(s) |
|---|---|---|
| `IIdentifierQuoter` | Providers | `Quote(string)`, `Qualify(ObjectIdentity)` — **the one member worth implementing for real**: Postgres quoting is `"ident"` with `""` escaping |
| `ISchemaReader` | Providers | `ReadAsync` → `DatabaseSchema` |
| `IScriptEmitter` | Providers | `Emit(source, target, diff, include?)` → `SyncScript` |
| `ICollationFactReader` | Providers | `ReadAsync(names)` → dict of `CollationFact` |
| `IVolumeReader` | Data | `ReadAsync` → `DatabaseVolume` |
| `IRowHashReader` | Data | `StreamAsync(...)` → `IAsyncEnumerable<KeyHashRow>` |
| `IRowDetailReader` | Data | `FetchAsync(...)` → `RowValues[]` |
| `IRowByValueReader` | Data | `FetchAsync(...)` → `RowValues[]` |
| `ITableFingerprintReader` | Data | `ReadAsync(tables)` → `TableFingerprint[]` |
| `IKeyUniquenessChecker` | Data | `ProfileAsync`, `CheckAsync` |
| `IDataScriptEmitter` | Scripting | `Emit(changes, maxInlineBytes)` → `ScriptStep[]` |

Server-level, directly on the provider: `ProbeAsync` → `ServerInfo`, `ListDatabasesAsync` →
`DatabaseSummary[]`, `DescribeDatabaseAsync` → `DatabaseDetail`.

Separate from the provider interface, wired on its own in `Program.cs:21`: `IScriptExecutor`
(`Apply/IScriptExecutor.cs`). The apply path needs a second piece outside `IDatabaseProvider` — see
*Friction* below. The stub provides a `PostgreSqlScriptExecutor` too so the shape is complete, even though
the API never wires it.

## Design of the stub

- New project `src/DbDelta.PostgreSql/`, mirroring `DbDelta.SqlServer.csproj`: references `Core` only, **no
  package reference** (no Npgsql — a compile proof needs no client). `TargetFramework`/`Nullable`/
  `ImplicitUsings` come from `Directory.Build.props`.
- `PostgreSqlProvider : IDatabaseProvider`, `Key => "postgresql"`, one stub class per returned interface,
  **one class per file** named after the type (`PostgreSqlSchemaReader`, etc.).
- `Quoter` is implemented for real — `"ident"`, double any embedded `"`, qualify as `"schema"."name"`. It
  is pure, needs no connection, and gives the proof one member that is genuinely a second engine's behaviour
  rather than a throw. Everything else throws.
- **Unimplemented members throw `NotSupportedException` with a consistent message** ("The PostgreSQL
  provider is a stub; <member> is not implemented yet."). A central `NotBuiltYet(member)` helper keeps the
  message uniform.
- **`TreatWarningsAsErrors=true`, so no stub method is `async`.** A throwing `async Task` trips CS1998.
  Throwing members return the task/enumerable type and throw synchronously (`Task<T>` body is just
  `throw`; an `IAsyncEnumerable<T>` method that throws before any `yield` is a plain method, not an
  iterator). Unused parameters do not warn, so signatures stay faithful.
- File-scoped namespace `DbDelta.PostgreSql;`. No XML doc comments. Comments only where a *why* is
  non-obvious (e.g. why the quoter is real and the rest is not).

## Verification (this is the whole point — it must be provable here)

- New `tests/DbDelta.PostgreSql.Tests/` (xUnit, matching the other test projects), referencing the stub.
- **Reflection test**: every `IDatabaseProvider` factory returns a non-null instance of the right interface,
  and every stub member except the quoter throws `NotSupportedException` when invoked. This is what proves
  the surface is *covered*, not merely that it compiles.
- **Quoter tests**: `Quote("a\"b")` → `"a""b"`, `Qualify` of a two-part identity → `"schema"."name"`.
- **The compiler is the primary proof.** `dotnet build` green with warnings-as-errors means a second class
  satisfied every interface with `Core` untouched. Add the project to `DbDelta.slnx` (both `src` and the
  new test under `tests`) so `dotnet build`/`dotnet test` pick it up.
- No LocalDB, no Postgres, no network. Fast half of the suite stays fast; nothing here is `Speed=Slow`.
- `Core` is not edited. If it turns out it must be, that edit is the headline finding, not a detail.

## Friction the abstraction already shows (report these; do not "fix" them silently)

1. **The API hard-wires one provider** — `Program.cs:17` `AddSingleton<IDatabaseProvider, SqlServerProvider>()`.
   There is no registry or per-connection selection, so a second provider cannot actually be *chosen* at
   runtime even once it exists. Runtime selection is deliberately out of scope here; the stub proves the
   abstraction compiles, and names this as the next step if PostgreSQL is ever pursued for real.
2. **`IScriptExecutor` sits outside `IDatabaseProvider`** and is wired separately. A provider is therefore
   not self-contained for the apply path — a second engine needs two registrations, not one. Worth noting
   as a shape question: should the executor be reachable *through* the provider?
3. **Several Data interfaces take SQL-Server-shaped assumptions in their types** (e.g. row hashing via a
   server-side digest). The stub does not resolve this; whether these types are genuinely engine-neutral is
   a question the stub surfaces but a real Postgres reader would answer.

## Note on the task workflow stages

Steps 4–6 of the standard workflow (before-screenshots, mockup sign-off, tester `.docx`) are **UI-facing and
do not apply here**: this change adds no screen, alters no screen, and has no tester-visible behaviour — the
app stays wired to SQL Server. The deliverable is a backend project plus a test project. Flagged rather than
silently skipped. Confirm before coding (step 7).
