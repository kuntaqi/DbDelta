# DbDelta

Compare two databases — structure and data — then sync one direction.

Schema compare for SQL Server is already solved for free by `sqlpackage`/DacFx. What isn't: free **data**
compare with per-table selective sync and guardrails that stop you writing to the wrong database. That's
the gap this fills.

## What it does

- Compares schema and data between two databases, one direction at a time
- Sync at three granularities: schema only, one table's data, or the whole database
- Per-table data modes — all rows, top N, a `WHERE` filter, or schema only — so a 21 GB table can come
  along as structure while a lookup table comes along as seed data
- Always produces a reviewable `.sql` script; applying it is a separate, opt-in, transactional step
- Shows footprint (how big the object is) separately from transfer (how much this sync actually moves)
- Draws the FK neighbourhood of a table with the sync plan overlaid, so you can see what a sync drags in
  and what it would break

SQL Server first; the diff engine is provider-abstracted so PostgreSQL can be added without touching it.

## Status

Design complete, implementation not started. See `docs/PLAN.md` for the engineering plan and
`docs/mockup/dbdelta-ux.html` for the seven-screen UX mockup — open it in a browser.

If you are an AI assistant working in this repo, read `CLAUDE.md` first.

## Stack

ASP.NET Core (.NET 10) API + React 19 / TypeScript / Vite SPA. Runs locally as a single-user tool.

## Safety

The tool never writes to the source database. Servers listed in `Safety:ReadOnlyServers` cannot be applied
to at all — script generation still works, so production stays reviewable but untouched. Applies run in one
transaction with `XACT_ABORT ON`, require typing the target database name, verify the target hasn't drifted
since the compare, and abort if deletes exceed a configured share of the table.
