# Redesign proposal — not the built app

These are **proposed** designs, rendered as images. Nothing here has been implemented, and the decision
to adopt any of it has not been made. Do not read a screen in this folder as evidence that a feature
exists — the same warning that applies to `docs/mockup/dbdelta-ux.html`'s history applies here, only
more strongly, because these were never built at all.

`current/` holds screenshots of the **running app** at the time the proposal was made, at 1440px:
connect, schema compare (with an object open inline), data compare, the key picker, sync plan, run log,
FK map. Those are real. Everything in the parent folder is not.

## What it proposes

| Image | Screen |
|---|---|
| `00-flow.png` | The flow end to end, plus what is kept / changed / deliberately not proposed |
| `01-visual-system.png` | Colour tokens, type scale, spacing, component treatments |
| `10`–`13` | Connections: default, first run, mid-test with an error, read-only Prod target |
| `20`–`22` | Schema compare: default with a difference open, loading, everything identical |
| `30`–`35` | Data compare: before the scan, scanning, all rows, Top N with deletes suppressed, and both key-picker cases |
| `40`–`43` | Sync plan: default, empty, the apply gates, blocked on a read-only server |
| `50`–`51` | Run log: with a drift abort expanded, and empty |
| `60` | FK map: neighbourhood at depth 2 |

The substantive changes are a persistent 320px plan rail on every screen, a permanent source → target
route bar, a Data compare screen that leads with the scan and triages its result, and an apply screen
with visual weight of its own. Tabs stay tabs — free navigation is kept deliberately.

## Drawn as real, but not built

Worth knowing before costing any of this:

- **Dependency closure** — the "pulled in for you" block naming what an FK forced into the plan. Core has
  the pieces; the API and UI do not create them. This is the expensive item in the proposal, not a
  layout change.
- **Saved connection profiles and recent pairs** — the largest flow gain on screen 1, and entirely new.
- **First-class exclusions with a revoke link**, the **Filter** row mode, and the FK map's **direction
  filter** — all three are in the "Dirancang, tidak dibangun" ledger at the end of the as-built mockup.

Numbers in the images are illustrative, chosen to match the measured figures in `docs/PLAN.md`
(263 tables, 4.4 s scan, 78 keyless, 17 skipped) so the readouts are plausible rather than invented.
Names follow the repo's generic stand-in rule; see `CLAUDE.md`.
