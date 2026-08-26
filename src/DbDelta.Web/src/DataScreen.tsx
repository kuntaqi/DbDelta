import { useEffect, useState } from 'react'
import {
  dataApi,
  formatBytes,
  planApi,
  type SelectedTable,
  type CompareResponse,
  type DataCompareResponse,
  type TableDataMode,
  type TableRow,
  type VolumeSummary,
} from './api'

const MODES: { value: TableDataMode; label: string }[] = [
  { value: 'AllRows', label: 'All rows' },
  { value: 'TopN', label: 'Top N' },
  { value: 'SchemaOnly', label: 'Schema only' },
]

const CLASS_TONE: Record<string, string> = { Insert: 'add', Update: 'chg', Delete: 'del' }
const CLASS_GLYPH: Record<string, string> = { Insert: '+', Update: '~', Delete: '−' }

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
  const [result, setResult] = useState<DataCompareResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [inPlan, setInPlan] = useState<SelectedTable[]>([])

  const picked = selected !== null && inPlan.some((s) => s.table === selected.qualifiedName)

  async function toggle(next: boolean) {
    if (!selected) return
    setBusy(true)
    setError(null)
    try {
      const response = await planApi.select(comparison.id, selected.qualifiedName, next, mode, topCount)
      setInPlan(response.selected)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
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

  useEffect(() => {
    if (!selected || !selected.hasKey || !selected.onBothSides) {
      setResult(null)
      return
    }

    setBusy(true)
    setError(null)
    dataApi
      .compare(comparison.id, selected.qualifiedName, mode, topCount, null)
      .then(setResult)
      .catch((e) => {
        setResult(null)
        setError(e instanceof Error ? e.message : String(e))
      })
      .finally(() => setBusy(false))
  }, [comparison.id, selected, mode, topCount])

  const selectedFootprint =
    volume?.tables.filter((t) => t.onBothSides).reduce((sum, t) => sum + t.sourceBytes, 0) ?? 0

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
            Tables <span className="plain rt">{volume?.tables.length ?? 0}</span>
          </div>
          <ul className="tree" style={{ fontSize: 13 }}>
            {volume?.tables.map((table) => (
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
                      <span className="why" style={{ color: 'var(--del)' }}>
                        no key
                      </span>
                    )}
                    {!table.onBothSides && <span className="why dim">source only</span>}
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

          {selected && (
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
                  <span className="chip on mono">{selected.keyColumns.join(', ') || '(none)'}</span>
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

              {result && result.rows.length > 0 && (
                <div style={{ overflowX: 'auto' }}>
                  <table className="grid">
                    <thead>
                      <tr>
                        <th style={{ width: 90 }}>Change</th>
                        <th style={{ width: 120 }}>{result.keyColumns.join(', ')}</th>
                        <th>Cells</th>
                      </tr>
                    </thead>
                    <tbody>
                      {result.rows.map((row) => (
                        <tr key={row.key}>
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
            ? 'No table data in the plan. Schema differences are selected for you; data is per table.'
            : `${inPlan.length} table(s) of data in the plan: ${inPlan.map((s) => s.table).join(', ')}`}
        </span>
        <span className="push" />
        {selected && selected.hasKey && selected.onBothSides && (
          <button
            type="button"
            className={`btn ${picked ? '' : 'primary'}`}
            disabled={mode === 'SchemaOnly' || busy}
            onClick={() => toggle(!picked)}
          >
            {picked ? 'Remove from plan' : 'Add this table to plan'}
          </button>
        )}
      </div>
    </div>
  )
}
