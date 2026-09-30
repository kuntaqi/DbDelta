# Contributing to DbDelta

Thanks for looking. DbDelta writes to databases, so the bar for a change is that it is correct and that
its tests prove it, not that it looks right.

## Before you start

For anything bigger than a small fix, open an issue first and describe what you want to change and why.
`docs/PLAN.md` is the source of truth for the design and records decisions that are already locked; a
change that reopens one of them needs that conversation before it needs code.

`CLAUDE.md` is the contributor guide. Despite the name it is written for people as much as for AI
assistants: it covers the conventions, the seven ideas the design rests on, and the traps that have
already cost time.

## Setting up

You need the .NET 10 SDK, Node 20 or newer, and SQL Server LocalDB for the integration tests. The README
covers the first build: the SPA has to be built once before the API serves anything.

## Tests

```
dotnet test --filter "Speed!=Slow"    # the inner loop
dotnet test                            # the whole suite: run this before opening a pull request
```

Integration tests run against LocalDB only, never a shared server, and they **skip** when LocalDB is
absent. A green run with skips proves nothing about the SQL Server provider, so check for them. A change to
the emitter or the apply path needs a test that applies its output to a real database.

## Conventions

- `async`/`await` for new backend code.
- File-scoped namespaces (`namespace X;`) and one type per file.
- Typed request models; no `dynamic` for a known request shape.
- Controllers stay thin and call a service.
- Comments are terse and explain a non-obvious *why*. No XML doc comments.
- Commit messages are one line, written as a sentence that says what the change does.

## Never commit real environment names

Every example, fixture and screenshot uses generic stand-ins: `DBSERVER-PROD` / `DBSERVER-UAT` /
`DBSERVER-DEV` for servers and `AppProd` / `AppUat` / `AppDev` for databases. Real server names, database
names and internal object names from any organisation's environment do not belong in this repository.
Your own server-name patterns go in `src/DbDelta.Api/appsettings.Local.json`, which is gitignored.

## Pull requests

Keep a pull request to one change, say what it fixes and how you verified it, and make sure the full
suite passes. The build runs on every pull request and has to be green before a merge.

By contributing you agree that your contribution is licensed under the MIT License that covers this
project.
