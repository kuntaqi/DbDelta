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

Numbers in the images are illustrative, chosen to match the measured figures in `docs/PLAN.md`
(263 tables, 4.4 s scan, 78 keyless, 17 skipped) so the readouts are plausible rather than invented.
Names follow the repo's generic stand-in rule; see `CLAUDE.md`.

---

# Audit against our own rules

The proposal was checked against `docs/PLAN.md`, `CLAUDE.md`, and the *Dirancang, tidak dibangun* ledger
at the end of `docs/mockup/dbdelta-ux.html`. Adopt nothing from this folder without reading this section
first — several screens are drawn in ways that contradict decisions already taken, and the contradictions
are not visually obvious.

## Collides with a rule or a recorded decision

**1. Saved connection profiles — the rule is "no credential storage", not "no profiles".**
The ledger's reason for not building them reads: *no credential storage, the password is retyped every
session.* Profiles themselves are sanctioned — `CLAUDE.md` reserves `%APPDATA%\DbDelta\profiles.json` and
`.gitignore` already excludes it. So a profile may hold server, database and auth mode, and must never
hold the password. The mockup's caption ("passwords are re-asked, never stored in the repo") applies the
wrong test: the repo was never the concern, the disk is.

Related documentation defect: `docs/PLAN.md`'s UI-screens list still names "saved profiles" on screen 1,
which contradicts the ledger. One of the two should be corrected.

**2. A database-size readout on the connection card** (`10`–`13`, and the route bar on every screen).
Ledger item 8 decided the opposite: size belongs **per table on the data screen, not on the connection
card**. The reason still holds — it is an extra catalog round trip taken before the user has asked for
anything.

**3. An empty plan shown as applied.** `50-run-log-default.png` lists *"Applied 0 steps · COMMITTED"* and
argues for keeping it visible. An empty plan is **refused**, not applied — which the proposal's own
Connections screen states correctly ("An empty plan is refused, not applied"). The two screens disagree
with each other.

**4. "Re-run this script" in the run log.** Re-applying a stored script skips the drift re-verification
that gate 4 exists to enforce. `PLAN.md`: a drift aborts the apply and asks for a fresh compare rather
than running against a database the plan no longer understands. Re-running is exactly the path that guard
was built to close.

**5. A "staged bulk path" / "bulk bundle" past the ~100 MB script cliff** (`40`, and a run log entry).
This is the heaviest item. Locked decision 3 is that a reviewable `.sql` script is **always** emitted and
apply is opt-in. A bulk path is a second execution mechanism with no review artefact, so it changes the
safety model rather than the UI. It appears nowhere in `PLAN.md`.

**6. Factual error in a caption.** The run log screen says *"Runs are session state"*. `RunLogStore`
writes `%APPDATA%\DbDelta\runs\*.json`, so runs already survive a restart. What is lost on restart is the
**compare session** — including key overrides and plan picks — not the run log.

## Aligned with the plan, but not built — cost, not conflict

These are consistent with `PLAN.md` and simply do not exist yet. Ordered roughly by expense:

- **Dependency closure** — the "pulled in for you" block naming what an FK forced into the plan. `PLAN.md`
  *requires* the UI to show this, so the proposal is right to draw it; the work runs Core → API → UI and is
  the single most expensive item in the folder.
- **Scope escalation** — the `Picked items | Entire database` toggle. Scope lattice exists in Core only.
- **First-class exclusions with a revoke link** — the type exists in Core; nothing creates one.
- **The `Filter` row mode** — supported by the backend, with no UI input today.
- **The FK map's direction filter** — only focus and depth are adjustable.
- **A Strategy panel** — the emitter picks a strategy but does not report why. The proposal shows a trace of
  this ("ALTER, no rebuild — widening NVARCHAR(20) to (40) keeps the data") rather than a full panel.

## New capabilities nobody has specified

Not forbidden, not planned — price them before adopting:

- **"REFERENCED BY DBO.SITE ×12"** on a delete row. The tool does FK *ordering*, not per-row inbound
  reference counts; this needs a probe per key.
- **Inserts / Updates / Deletes filter chips** over the row grid. This revives ledger item 6 as a filter,
  where the decision had been one combined grid with a Change column. It also carries a correctness trap:
  the chip counts are exact because they come from the hash pass, but only 200 rows are ever fetched, so
  the filter has to run server-side per class or the counts promise rows the grid cannot show.
- Smaller ones: an estimated apply duration, `Clear log`, `Copy summary`, a name filter box, an
  indeterminate ("partial") checkbox state, and clearing the plan when direction is swapped.

## What the audit cleared

Worth recording, so the list above is not mistaken for a verdict on the whole proposal:

- No component library and no web font — the type stack is Segoe UI + Cascadia Mono, both stock on Windows.
- **No per-row checkboxes.** Selection stops at table level, which is the decision on record.
- Not a wizard; free navigation between tabs is kept.
- No whole-schema ER diagram; the FK map stays a neighbourhood.
- A Prod target is blocked, and script-only download for a read-only server is preserved.
- Limited row modes suppress deletes, and say so, everywhere the mode appears.
- Nothing is pre-ticked on load.
