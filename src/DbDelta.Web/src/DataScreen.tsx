import { useEffect, useState } from 'react'
import {
  api,
  dataApi,
  formatBytes,
  planApi,
  keyApi,
  scanApi,
  type KeyChoiceResponse,
  type SelectedTable,
  type TableScanResponse,
  type CompareResponse,
  type DataCompareResponse,
  type TableDataMode,
  type TableRow,
  type VolumeSummary,
  type PlanEstimateResponse,
} from './api'

const MODES: { value: TableDataMode; label: string }[] = [
  { value: 'AllRows', label: 'All rows' },
  { value: 'TopN', label: 'Top N' },
  { value: 'Filter', label: 'Filter' },
  { value: 'SchemaOnly', label: 'Schema only' },
]

const CLASS_TONE: Record<string, string> = { Insert: 'add', Update: 'chg', Delete: 'del' }
const CLASS_GLYPH: Record<string, string> = { Insert: '+', Update: '~', Delete: '−' }

// The scan knows a table differs and by how many rows in total; which rows is a question for the
// exact compare, so the label says "differs" rather than inventing a breakdown.
function rowDeltaLabel(delta: number): string {
  if (delta === 0) return 'differs'
  return `differs (${delta > 0 ? '+' : ''}${delta.toLocaleString()} rows)`
}

function sizeTone(bytes: number): string {
  if (bytes > 1024 * 1024 * 1024) return 'var(--del)'
  if (bytes > 100 * 1024 * 1024) return 'var(--chg)'
  return 'var(--ink-3)'
}

export function DataScreen({ comparison }: { comparison: CompareResponse }) {
  const [volume, setVolume] = useState<VolumeSummary | null>(null)
  const [selected, setSelected] = useState<TableRow | null>(null)
  const [mode, setMode] = useState<TableDataMode>('AllRows')
  const [topCount, setTopCount] = useState(100)
  // Two values, because a predicate is not usable half typed: the draft is what is in the box, the
  // committed one is what has been compared with. Comparing on every keystroke would run a query per
  // character and show a validation error for every unfinished word.
  const [filterDraft, setFilterDraft] = useState('')
  const [filter, setFilter] = useState('')
  const [result, setResult] = useState<DataCompareResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [inPlan, setInPlan] = useState<SelectedTable[]>([])
  const [scan, setScan] = useState<TableScanResponse | null>(null)
  const [scanning, setScanning] = useState(false)
  const [onlyDiffering, setOnlyDiffering] = useState(true)
  const [scanLimitMb, setScanLimitMb] = useState(200)
  const [keyChoice, setKeyChoice] = useState<KeyChoiceResponse | null>(null)
  const [draftKey, setDraftKey] = useState<string[]>([])
  const [estimate, setEstimate] = useState<PlanEstimateResponse | null>(null)

  async function run<T>(action: () => Promise<T>, then: (value: T) => void) {
    setBusy(true)
    setError(null)
    try {
      then(await action())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const entry = selected === null ? undefined : inPlan.find((s) => s.table === selected.qualifiedName)
  const picked = entry !== undefined
  // null is the whole table; a list is a narrowing to those keys, empty included.
  const pickedRows = entry?.pickedRows ?? null
  const narrowed = pickedRows !== null
  const changedCount = result === null ? 0 : result.insertCount + result.updateCount + result.deleteCount
  const scanned = new Map(scan?.tables.map((t) => [t.table, t]) ?? [])

  // Which tables differ is only knowable by comparing them, so the list stays complete until a scan
  // has actually run. Hiding rows before then would be hiding rows on no evidence.
  const listed = (volume?.tables ?? []).filter((table) => {
    if (!scan || !onlyDiffering) return true
    const row = scanned.get(table.qualifiedName)
    return row ? row.differs || !row.comparable : true
  })

  async function runScan() {
    setScanning(true)
    setError(null)
    try {
      setScan(await scanApi.run(comparison.id, scanLimitMb * 1024 * 1024))
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setScanning(false)
    }
  }

  async function toggle(next: boolean, rows: string[] | null = null) {
    if (!selected) return
    setBusy(true)
    setError(null)
    try {
      const response = await planApi.select(comparison.id, selected.qualifiedName, next, mode, topCount, mode === 'Filter' ? filter : null, rows)
      setInPlan(response.selected)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  // Switching between the whole table and a list of rows. Turning narrowing on starts from nothing rather
  // than from everything on screen: the screen holds the first 200 differences, so "everything shown"
  // would silently mean "and none of the other 4,800".
  function setNarrowing(on: boolean) {
    void toggle(true, on ? [] : null)
  }

  function toggleRow(key: string) {
    if (pickedRows === null) return
    void toggle(
      true,
      pickedRows.includes(key) ? pickedRows.filter((k) => k !== key) : [...pickedRows, key],
    )
  }

  useEffect(() => {
    planApi
      .selection(comparison.id)
      .then((s) => setInPlan(s.selected))
      .catch(() => setInPlan([]))
  }, [comparison.id])

  useEffect(() => {
    dataApi
      .volume(comparison.id)
      .then((v) => {
        setVolume(v)
        const first = v.tables.find((t) => t.onBothSides && t.hasKey)
        if (first) setSelected(first)
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [comparison.id])

  // A keyless table needs its candidate columns before anything can be offered, so that request goes
  // out as soon as one is selected.
  useEffect(() => {
    setDraftKey([])
    setKeyChoice(null)

    if (!selected || !selected.onBothSides || selected.hasKey) {
      return
    }

    keyApi
      .options(comparison.id, selected.qualifiedName)
      .then((options) => {
        setKeyChoice(options)
        // Ticked, not applied: the suggestion fills the picker so the user is not left guessing, but
        // the confirming click is still theirs to make.
        setDraftKey(options.recommended)
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [comparison.id, selected])

  useEffect(() => {
    const hasKey = selected?.hasKey || (keyChoice?.chosen.length ?? 0) > 0

    if (!selected || !hasKey || !selected.onBothSides) {
      setResult(null)
      return
    }

    // Filter mode with nothing committed yet is not "compare everything" — it is a question that has not
    // been asked. Comparing all rows here would answer a different one.
    if (mode === 'Filter' && filter.length === 0) {
      setResult(null)
      return
    }

    setBusy(true)
    setError(null)
    dataApi
      .compare(comparison.id, selected.qualifiedName, mode, topCount, mode === 'Filter' ? filter : null)
      .then(setResult)
      .catch((e) => {
        setResult(null)
        setError(e instanceof Error ? e.message : String(e))
      })
      .finally(() => setBusy(false))
  }, [comparison.id, selected, mode, topCount, filter, keyChoice])

  const selectedFootprint =
    volume?.tables.filter((t) => t.onBothSides).reduce((sum, t) => sum + t.sourceBytes, 0) ?? 0

  // A declared key is a different situation from a guessed one: the schema already states it, so the
  // picker says so instead of asking the user to rediscover it.
  const declaredBy =
    keyChoice?.candidates.find((c) => c.declaredBy && keyChoice.recommended.includes(c.column))
      ?.declaredBy ?? null
  const declaredKey =
    declaredBy && keyChoice ? `${declaredBy} (${keyChoice.recommended.join(', ')})` : null

  return (
    <div className="app">
      <div className="app-bar">
        <span className="mono">data compare</span>
        <span className="dim mono push">
          {volume ? `${volume.tables.length} tables` : 'loading…'}
        </span>
      </div>

      <div className="split">
        <div className="pane-l">
          <div className="pane-hd">
            Tables
            <span className="plain rt">
              {scan ? `${listed.length} of ${volume?.tables.length ?? 0}` : (volume?.tables.length ?? 0)}
            </span>
          </div>

          <div style={{ padding: '10px 14px', borderBottom: '1px solid var(--line)', display: 'flex', flexDirection: 'column', gap: 8 }}>
            <button type="button" className="btn" onClick={runScan} disabled={scanning || !volume}>
              {scanning ? 'Scanning…' : scan ? 'Rescan all tables' : 'Scan all tables for differences'}
            </button>

            <label className="dim" style={{ fontSize: 11.5, display: "flex", gap: 6, alignItems: "center" }}>
              Skip tables over
              <input
                className="field"
                type="number"
                min={1}
                value={scanLimitMb}
                onChange={(e) => setScanLimitMb(Number(e.target.value) || 1)}
                style={{ width: 78 }}
                aria-label="Scan size limit in megabytes"
              />
              MB
            </label>

            {!scan && !scanning && (
              <p className="dim" style={{ margin: 0, fontSize: 11.5 }}>
                Every table is listed until a scan runs. Finding which ones differ means comparing them —
                one hash pass each, no row data moved.
              </p>
            )}

            {scan && (
              <>
                <button
                  type="button"
                  className={`chip ${onlyDiffering ? 'on' : ''}`}
                  onClick={() => setOnlyDiffering(!onlyDiffering)}
                >
                  Hide matching tables <span className="n">{scan.scanned - scan.differing}</span>
                </button>
                <p className="dim" style={{ margin: 0, fontSize: 11.5 }}>
                  {scan.differing} differ · {scan.scanned - scan.differing} match · {scan.notComparable} not
                  comparable {scan.skipped > 0 && `(${scan.skipped} too large)`} · scanned in{" "}
                  {(scan.durationMs / 1000).toFixed(1)}s
                </p>
              </>
            )}
          </div>

          <ul className="tree" style={{ fontSize: 13 }}>
            {listed.map((table) => (
              <li key={table.qualifiedName}>
                <button
                  type="button"
                  className={`it ${selected?.qualifiedName === table.qualifiedName ? 'on' : ''}`}
                  style={{ paddingLeft: 14, flexDirection: 'column', alignItems: 'stretch', gap: 3 }}
                  onClick={() => setSelected(table)}
                  disabled={!table.onBothSides}
                >
                  <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <span className={`nm ${table.onBothSides ? '' : 'dim'}`}>{table.qualifiedName}</span>
                    {inPlan.some((s) => s.table === table.qualifiedName) && (
                      <span className="badge dev">in plan</span>
                    )}
                    {!table.hasKey && (
                      <span
                        className="why"
                        style={{ color: table.declaredKey.length > 0 ? 'var(--chg)' : 'var(--del)' }}
                        title={
                          table.declaredKey.length > 0
                            ? `A key is declared here (${table.declaredKey.join(', ')}) — it is just not the primary key.`
                            : 'Nothing identifies a row yet.'
                        }
                      >
                        {table.declaredKey.length > 0 ? 'key not set' : 'no key'}
                      </span>
                    )}
                    {!table.onBothSides && <span className="why dim">source only</span>}
                    {scanned.get(table.qualifiedName)?.differs && (
                      <span className="why" style={{ color: 'var(--chg)' }}>
                        {rowDeltaLabel(scanned.get(table.qualifiedName)!.rowDelta)}
                      </span>
                    )}
                    {scanned.get(table.qualifiedName)?.comparable === false && (
                      <span className="why dim">{scanned.get(table.qualifiedName)!.reason}</span>
                    )}
                  </span>
                  <span
                    className="mono"
                    style={{ fontSize: 11.5, color: sizeTone(table.sourceBytes), textAlign: 'left' }}
                  >
                    {formatBytes(table.sourceBytes)} · {table.sourceRows.toLocaleString()} rows
                  </span>
                </button>
              </li>
            ))}
          </ul>

          {volume && (
            <div style={{ padding: '12px 14px', borderTop: '1px solid var(--line)' }}>
              <div className="tiles" style={{ gridTemplateColumns: '1fr 1fr' }}>
                <div className="tile">
                  <div className="k">Footprint</div>
                  <div className="v" style={{ fontSize: 17 }}>
                    {formatBytes(selectedFootprint)}
                  </div>
                </div>
                <div className="tile">
                  <div className="k">Transfer</div>
                  <div className="v" style={{ fontSize: 17, color: 'var(--accent)' }}>
                    {formatBytes(result?.transferBytesEstimate ?? 0)}
                  </div>
                </div>
              </div>
              <p className="dim" style={{ margin: '8px 0 0', fontSize: 11.5 }}>
                Footprint is how big the tables are. Transfer is what this sync would actually move.
              </p>
            </div>
          )}
        </div>

        <div>
          {!selected && <div style={{ padding: 20 }} className="dim">Pick a table on the left.</div>}

          {/* Silence was the old behaviour here: clicking a keyless table blanked the pane with no
              explanation. A table that cannot be compared now says why, and offers the way out. */}
          {selected && !selected.onBothSides && (
            <div style={{ padding: 20 }}>
              <div className="warnline warn">
                <span className="g">!</span>
                <div>
                  <b>{selected.qualifiedName}</b> exists only on the source, so there is no target data to
                  compare against. Sync its schema first, then it becomes comparable.
                </div>
              </div>
            </div>
          )}

          {selected && selected.onBothSides && !selected.hasKey && !keyChoice?.chosen.length && (
            <div style={{ padding: '16px 16px 0' }}>
              <div className="warnline warn" style={{ marginBottom: 12 }}>
                <span className="g">!</span>
                <div>
                  <b>{selected.qualifiedName}</b> has no primary key, so DbDelta cannot tell one row from
                  another.{' '}
                  {declaredKey
                    ? `The schema does declare a key here — ${declaredKey} — it is just not the primary key. Confirm it and the compare runs.`
                    : keyChoice?.probed
                      ? `Nothing is declared, so every column was measured across ${keyChoice.rowCount.toLocaleString()} row${keyChoice.rowCount === 1 ? '' : 's'}. Pick the columns that identify a row; they are checked on both sides before anything is compared.`
                      : 'Pick the columns that identify a row — they are checked for uniqueness on both sides before anything is compared.'}
                </div>
              </div>

              {keyChoice?.problem && (keyChoice.rejected || !keyChoice.probed) && (
                <div className="warnline" style={{ marginBottom: 12 }}>
                  <span className="g">!</span>
                  <div>{keyChoice.problem}</div>
                </div>
              )}

              <div className="row" style={{ flexWrap: 'wrap', gap: 6, marginBottom: 12 }}>
                {keyChoice?.candidates.map((candidate) => (
                  <button
                    key={candidate.column}
                    type="button"
                    className={`chip ${draftKey.includes(candidate.column) ? 'on' : ''}`}
                    style={{
                      alignItems: 'flex-start',
                      opacity: candidate.unique === false && !draftKey.includes(candidate.column) ? 0.55 : 1,
                    }}
                    onClick={() =>
                      setDraftKey(
                        draftKey.includes(candidate.column)
                          ? draftKey.filter((c) => c !== candidate.column)
                          : [...draftKey, candidate.column],
                      )
                    }
                  >
                    <span className="mono">{candidate.column}</span>
                    <span className="dim" style={{ fontSize: 11 }}>
                      {candidate.dataType}
                      {candidate.nullable ? ' · null' : ''}
                    </span>
                    <span
                      className="dim"
                      style={{
                        fontSize: 11,
                        color:
                          candidate.declaredBy || candidate.unique === true
                            ? 'var(--ok, #2f7d32)'
                            : undefined,
                      }}
                    >
                      {candidate.note}
                    </span>
                  </button>
                ))}
              </div>

              <div className="row">
                <button
                  type="button"
                  className="btn primary"
                  disabled={draftKey.length === 0 || busy}
                  onClick={() =>
                    run(
                      () => keyApi.choose(comparison.id, selected.qualifiedName, draftKey),
                      (choice) => {
                        setKeyChoice(choice)
                        // The table list took its key from the primary key when it loaded, so a key
                        // accepted now has to reach it or the row keeps saying the key is not set.
                        if (choice.chosen.length > 0) {
                          setVolume((current) =>
                            current === null
                              ? current
                              : {
                                  ...current,
                                  tables: current.tables.map((t) =>
                                    t.qualifiedName === choice.table
                                      ? { ...t, hasKey: true, keyColumns: choice.chosen }
                                      : t,
                                  ),
                                },
                          )
                        }
                      },
                    )
                  }
                >
                  {busy ? 'Checking…' : 'Use these as the key'}
                </button>
                <span className="dim" style={{ fontSize: 11.5 }}>
                  {draftKey.length === 0
                    ? 'Nothing picked yet.'
                    : `Key: ${draftKey.join(', ')}${
                        keyChoice && sameSet(draftKey, keyChoice.recommended) ? ' — suggested' : ''
                      }`}
                </span>
              </div>
            </div>
          )}

          {selected && selected.onBothSides && (selected.hasKey || (keyChoice?.chosen.length ?? 0) > 0) && (
            <>
              <div
                style={{
                  padding: '14px 16px',
                  display: 'flex',
                  flexDirection: 'column',
                  gap: 12,
                  borderBottom: '1px solid var(--line)',
                }}
              >
                <div className="row" style={{ flexWrap: 'wrap' }}>
                  <span className="mono" style={{ fontSize: 15, fontWeight: 600 }}>
                    {selected.qualifiedName}
                  </span>
                  <span className="badge unknown">src {selected.sourceRows.toLocaleString()}</span>
                  <span className="badge unknown">tgt {selected.targetRows.toLocaleString()}</span>
                  <span className="push" />
                  <span className="lbl" style={{ margin: 0 }}>Key</span>
                  <span className="chip on mono">
                    {(keyChoice?.chosen.length ? keyChoice.chosen : selected.keyColumns).join(', ') ||
                      '(none)'}
                  </span>
                </div>

                <div className="row" style={{ flexWrap: 'wrap' }}>
                  <span className="lbl" style={{ margin: 0 }}>Data mode</span>
                  {MODES.map((m) => (
                    <button
                      key={m.value}
                      type="button"
                      className={`chip ${mode === m.value ? 'on' : ''}`}
                      onClick={() => setMode(m.value)}
                    >
                      {m.label}
                    </button>
                  ))}
                  {mode === 'TopN' && (
                    <input
                      className="field"
                      type="number"
                      min={1}
                      value={topCount}
                      onChange={(e) => setTopCount(Number(e.target.value) || 1)}
                      style={{ width: 90 }}
                      aria-label="Top row count"
                    />
                  )}
                </div>

                {mode === 'Filter' && (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                    <div className="row" style={{ flexWrap: 'wrap' }}>
                      <span className="lbl" style={{ margin: 0 }}>WHERE</span>
                      <input
                        className="field mono"
                        style={{ flex: 1, minWidth: 260 }}
                        placeholder="Segment = 'Retail' AND RatingBand >= 3"
                        value={filterDraft}
                        spellCheck={false}
                        onChange={(e) => setFilterDraft(e.target.value)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter') setFilter(filterDraft.trim())
                        }}
                        aria-label="Filter predicate"
                      />
                      <button
                        type="button"
                        className="chip"
                        disabled={filterDraft.trim() === filter}
                        onClick={() => setFilter(filterDraft.trim())}
                      >
                        Apply filter
                      </button>
                    </div>
                    <p className="dim" style={{ margin: 0, fontSize: 11.5 }}>
                      Columns and constants only &mdash; no functions or subqueries, because a compare has to
                      stay a read on both sides. The predicate runs against source and target alike, so
                      deletes are suppressed: a row outside the filter is not a row that was removed.
                    </p>
                  </div>
                )}

                {mode === 'Filter' && filter.length === 0 && (
                  <div className="warnline info">
                    <span className="g">i</span>
                    <div>Nothing compared yet. Type a predicate and apply it.</div>
                  </div>
                )}

                {result && result.excludedColumns.length > 0 && (
                  <div className="row" style={{ flexWrap: 'wrap', gap: 7 }}>
                    <span className="lbl" style={{ margin: 0 }}>Compared</span>
                    <span className="chip">
                      {result.comparedColumns.length} of {result.comparedColumns.length + result.excludedColumns.length} columns
                    </span>
                    {result.excludedColumns.map((column) => (
                      <span className="chip mono dim" key={column.column}>
                        {column.column} <span style={{ fontFamily: 'var(--sans)' }}>· {column.source}</span>
                      </span>
                    ))}
                  </div>
                )}

                {result?.warning && (
                  <div className="warnline warn">
                    <span className="g">!</span>
                    <div>{result.warning}</div>
                  </div>
                )}

                {error && (
                  <div className="warnline">
                    <span className="g">!</span>
                    <div>{error}</div>
                  </div>
                )}

                {estimate && (
                  <div className={`warnline ${estimate.verdict === 'Within' ? 'info' : 'warn'}`}>
                    <span className="g">{estimate.verdict === 'Within' ? '◇' : '!'}</span>
                    <div>
                      <b>
                        {estimate.rowsAreExact
                          ? `This plan would produce ${formatBytes(estimate.maxBytes)}.`
                          : `This plan would produce between ${formatBytes(estimate.minBytes)} and ${formatBytes(estimate.maxBytes)}.`}
                      </b>{' '}
                      {formatBytes(estimate.schemaBytes)} of that is schema, measured rather than estimated.
                      {estimate.tables > 0 && (
                        <>
                          {' '}
                          {estimate.minRows === estimate.maxRows
                            ? `${estimate.maxRows.toLocaleString()} row(s)`
                            : `Between ${estimate.minRows.toLocaleString()} and ${estimate.maxRows.toLocaleString()} rows`}{' '}
                          would move across {estimate.tables} table(s).
                        </>
                      )}
                      {estimate.verdict === 'Exceeds' && (
                        <>
                          {' '}
                          <b>
                            That is past the {formatBytes(estimate.reviewableLimitBytes)} reviewable limit even
                            at the low end.
                          </b>{' '}
                          Narrow the plan, or download the script instead of reading it here.
                        </>
                      )}
                      {estimate.verdict === 'Possibly' && (
                        <>
                          {' '}
                          <b>
                            The high end is past the {formatBytes(estimate.reviewableLimitBytes)} reviewable
                            limit.
                          </b>{' '}
                          Whether it gets there depends on how many rows actually differ, which only comparing
                          them establishes.
                        </>
                      )}
                      <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                        {estimate.notes.map((note) => (
                          <li key={note} style={{ fontSize: 11.5 }}>
                            {note}
                          </li>
                        ))}
                      </ul>
                    </div>
                  </div>
                )}

                {result && (
                  <div className="tiles">
                    <div className="tile">
                      <div className="k">Insert</div>
                      <div className="v" style={{ color: 'var(--add)' }}>{result.insertCount}</div>
                      <div className="s">missing on target</div>
                    </div>
                    <div className="tile">
                      <div className="k">Update</div>
                      <div className="v" style={{ color: 'var(--chg)' }}>{result.updateCount}</div>
                      <div className="s">values differ</div>
                    </div>
                    <div className="tile">
                      <div className="k">Delete</div>
                      <div className="v" style={{ color: result.deletesSuppressed ? 'var(--ink-3)' : 'var(--del)' }}>
                        {result.deletesSuppressed ? '—' : result.deleteCount}
                      </div>
                      <div className="s">{result.deletesSuppressed ? 'suppressed' : 'target only'}</div>
                    </div>
                    <div className="tile">
                      <div className="k">Same</div>
                      <div className="v">{result.sameCount}</div>
                      <div className="s">no action</div>
                    </div>
                  </div>
                )}
              </div>

              {busy && <div style={{ padding: 20 }} className="dim">Comparing…</div>}

              {result && result.rows.length > 0 && picked && mode === 'AllRows' && (
                <div className="actionbar" style={{ borderTop: 0 }}>
                  <span className="guard">
                    <span className={`g ${narrowed ? 'add' : 'same'}`}>{narrowed ? '✓' : '◇'}</span>
                    {narrowed
                      ? `${pickedRows!.length} of ${changedCount} changed row(s) picked. Only these go in the plan.`
                      : `All ${changedCount} changed row(s) go in the plan.`}
                  </span>
                  <span className="push" />
                  {/* The list stops at 200 rows, so picking through a large table is not something this
                      offers to do. Saying it here is the difference between a limit and a trap. */}
                  {narrowed && result.rows.length < changedCount && (
                    <span className="dim" style={{ marginRight: 10 }}>
                      showing the first {result.rows.length}; the rest cannot be ticked
                    </span>
                  )}
                  <button type="button" className="btn" disabled={busy} onClick={() => setNarrowing(!narrowed)}>
                    {narrowed ? 'Take the whole table' : 'Pick rows instead'}
                  </button>
                </div>
              )}

              {result && result.rows.length > 0 && (
                <div style={{ overflowX: 'auto' }}>
                  <table className="grid">
                    <thead>
                      <tr>
                        {narrowed && <th style={{ width: 34 }} />}
                        <th style={{ width: 90 }}>Change</th>
                        <th style={{ width: 120 }}>{result.keyColumns.join(', ')}</th>
                        <th>Cells</th>
                      </tr>
                    </thead>
                    <tbody>
                      {result.rows.map((row) => (
                        <tr key={row.key}>
                          {narrowed && (
                            <td>
                              <input
                                type="checkbox"
                                aria-label={`Include ${row.display}`}
                                checked={pickedRows!.includes(row.key)}
                                disabled={busy}
                                onChange={() => toggleRow(row.key)}
                              />
                            </td>
                          )}
                          <td>
                            <span className={`g ${CLASS_TONE[row.classification]}`}>
                              {CLASS_GLYPH[row.classification]}
                            </span>
                            <span style={{ fontFamily: 'var(--sans)', marginLeft: 4 }}>{row.classification}</span>
                          </td>
                          <td>{row.display}</td>
                          <td>
                            {row.changes.map((cell) => (
                              <div key={cell.column}>
                                {cell.column}{' '}
                                <span style={{ color: 'var(--del)', textDecoration: 'line-through' }}>
                                  {cell.target ?? 'NULL'}
                                </span>
                                <span style={{ color: 'var(--ink-3)', padding: '0 5px' }}>&rarr;</span>
                                <span style={{ color: 'var(--add)' }}>{cell.source ?? 'NULL'}</span>
                              </div>
                            ))}
                            {row.unchangedColumns > 0 && (
                              <div style={{ fontFamily: 'var(--sans)', fontSize: 11.5, color: 'var(--ink-3)' }}>
                                + {row.unchangedColumns} unchanged column(s)
                              </div>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              {result && result.rows.length === 0 && !busy && (
                <div style={{ padding: 20 }} className="dim">
                  {result.mode === 'SchemaOnly'
                    ? 'Schema only: structure is synced, no rows move.'
                    : 'No row differences for this table.'}
                </div>
              )}
            </>
          )}
        </div>
      </div>

      <div className="actionbar">
        <span className="guard">
          <span className={`g ${inPlan.length > 0 ? 'add' : 'same'}`}>
            {inPlan.length > 0 ? '✓' : '◇'}
          </span>
          {inPlan.length === 0
            ? 'No table data in the plan. Nothing is selected for you — pick tables here and schema objects on Schema compare.'
            : `${inPlan.length} table(s) of data in the plan: ${inPlan.map((s) => s.table).join(', ')}`}
        </span>
        <span className="push" />
        {/* Before building the script rather than after it. Building one fetches every changed row from
            both databases, which is minutes on a large plan; this is one round trip for the row counts. */}
        <button
          type="button"
          className="chip"
          disabled={busy}
          onClick={() => run(() => api.estimate(comparison.id), setEstimate)}
        >
          Estimate the script
        </button>
        {selected && selected.hasKey && selected.onBothSides && (
          <button
            type="button"
            className={`btn ${picked ? '' : 'primary'}`}
            // A filter with no result behind it — never applied, or refused — has nothing to add to the plan.
            disabled={mode === 'SchemaOnly' || busy || (mode === 'Filter' && result === null)}
            onClick={() => toggle(!picked)}
          >
            {picked ? 'Remove from plan' : 'Add this table to plan'}
          </button>
        )}
      </div>
    </div>
  )
}

function sameSet(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((c) => right.includes(c))
}
