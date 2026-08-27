import { useEffect, useState } from 'react'
import { fkApi, type CompareResponse, type FkDirection, type FkMapResponse, type FkNode } from './api'

const NODE_W = 190
const NODE_H = 54
const BAND_GAP = 150
const H_GAP = 30

const STATE_CLASS: Record<string, string> = {
  Focus: 'nd-focus',
  InPlan: 'nd-plan',
  Untouched: 'nd',
  Excluded: 'nd-dim',
}

// Depth 1–2 lays out as a few horizontal bands, so the positions are computed here rather than
// pulling in a graph layout library to place three rows of boxes.
function layout(nodes: FkNode[]) {
  const bands = [...new Set(nodes.map((n) => n.band))].sort((a, b) => a - b)
  const placed = new Map<string, { x: number; y: number }>()
  let width = 0

  bands.forEach((band, bandIndex) => {
    const inBand = nodes.filter((n) => n.band === band)
    const rowWidth = inBand.length * NODE_W + (inBand.length - 1) * H_GAP
    width = Math.max(width, rowWidth)

    inBand.forEach((node, index) => {
      placed.set(node.qualifiedName, {
        x: index * (NODE_W + H_GAP) - rowWidth / 2,
        y: bandIndex * BAND_GAP,
      })
    })
  })

  const height = bands.length * BAND_GAP - (BAND_GAP - NODE_H) + 30
  return { placed, width: width + 80, height, bands }
}

export function FkMapScreen({ comparison }: { comparison: CompareResponse }) {
  const tables = comparison.objects.filter((o) => o.type === 'Table')
  const [focus, setFocus] = useState(tables[0]?.qualifiedName ?? '')
  const [depth, setDepth] = useState(1)
  // Both by default: at depth 1 everything fits and choosing would be a decision nobody needs to make.
  // The filter earns its keep at depth 3 on a table half the schema points at.
  const [direction, setDirection] = useState<FkDirection>('Both')
  const [map, setMap] = useState<FkMapResponse | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!focus) return
    fkApi
      .map(comparison.id, focus, depth, direction)
      .then((m) => {
        setMap(m)
        setError(null)
      })
      .catch((e) => {
        setMap(null)
        setError(e instanceof Error ? e.message : String(e))
      })
  }, [comparison.id, focus, depth, direction])

  const { placed, width, height } = map ? layout(map.nodes) : { placed: new Map<string, { x: number; y: number }>(), width: 700, height: 200 }
  const viewBox = `${-width / 2} -20 ${width} ${height}`

  return (
    <div className="app">
      <div className="app-bar">
        <span className="mono">FK map</span>
        <span className="dim">&middot;</span>
        <span className="mono">{focus}</span>
        <span className="dim mono push">{map ? `${map.nodes.length} tables in view` : 'loading…'}</span>
      </div>

      <div className="scopebar">
        <span className="lbl" style={{ margin: 0 }}>Focus</span>
        <select className="field" value={focus} onChange={(e) => setFocus(e.target.value)} style={{ width: 200 }}>
          {tables.map((table) => (
            <option key={table.id} value={table.qualifiedName}>
              {table.qualifiedName}
            </option>
          ))}
        </select>
        <span className="lbl" style={{ margin: 0 }}>Depth</span>
        {[1, 2, 3].map((value) => (
          <button
            key={value}
            type="button"
            className={`chip ${depth === value ? 'on' : ''}`}
            onClick={() => setDepth(value)}
          >
            {value}
          </button>
        ))}
        {/* Parents are what must exist first; children are what breaks if referenced rows are missing.
            Hiding one hides it from the drawing only — the notes below still count what is over there. */}
        <span className="lbl" style={{ margin: 0 }}>Show</span>
        {(['Both', 'Parents', 'Children'] as FkDirection[]).map((value) => (
          <button
            key={value}
            type="button"
            className={`chip ${direction === value ? 'on' : ''}`}
            title={
              value === 'Parents'
                ? 'What must exist before this table'
                : value === 'Children'
                  ? 'What breaks if this table is missing rows'
                  : 'Both directions'
            }
            onClick={() => setDirection(value)}
          >
            {value}
          </button>
        ))}
      </div>

      {error && (
        <div style={{ padding: 16 }}>
          <div className="warnline">
            <span className="g">!</span>
            <div>{error}</div>
          </div>
        </div>
      )}

      {map && (
        <>
          <div className="diagram">
            <figure>
              {/* Natural pixel size, scaled down only when it will not fit. Letting the viewBox
                  stretch to the container blows a two-node map up to four times its intended size. */}
              <svg
                viewBox={viewBox}
                width={width}
                height={height}
                role="img"
                aria-label={`Foreign key neighbourhood of ${map.focus}: ${map.nodes.length} tables and ${map.edges.length} relationships`}
                style={{ maxWidth: '100%', height: 'auto' }}
              >
                <defs>
                  <marker id="fk-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6" orient="auto">
                    <polygon points="0,0 10,5 0,10" style={{ fill: 'var(--ink-3)' }} />
                  </marker>
                </defs>

                {map.edges.map((edge, index) => {
                  const from = placed.get(edge.from)
                  const to = placed.get(edge.to)
                  if (!from || !to) return null

                  const x1 = from.x + NODE_W / 2
                  const y1 = from.y
                  const x2 = to.x + NODE_W / 2
                  const y2 = to.y + NODE_H
                  const midY = (y1 + y2) / 2

                  return (
                    <g key={index}>
                      <line
                        className={edge.nullable ? 'eg-opt' : 'eg'}
                        x1={x1}
                        y1={y1}
                        x2={x2}
                        y2={y2 + 4}
                        markerEnd="url(#fk-arrow)"
                      />
                      <text className="el" x={(x1 + x2) / 2} y={midY - 4} textAnchor="middle">
                        {edge.columns} · {edge.nullable ? 'NULL' : 'NOT NULL'}
                      </text>
                    </g>
                  )
                })}

                {map.nodes.map((node) => {
                  const position = placed.get(node.qualifiedName)
                  if (!position) return null

                  return (
                    <g key={node.qualifiedName}>
                      <rect
                        className={`nd ${STATE_CLASS[node.state] ?? ''}`}
                        x={position.x}
                        y={position.y}
                        width={NODE_W}
                        height={NODE_H}
                        rx="7"
                      />
                      <text className="nl" x={position.x + NODE_W / 2} y={position.y + 23}>
                        {node.qualifiedName}
                      </text>
                      <text className="ns" x={position.x + NODE_W / 2} y={position.y + 40}>
                        {node.note}
                      </text>
                    </g>
                  )
                })}
              </svg>
              <figcaption>
                Rows above the focus must exist before it; rows below reference it and break if its rows are
                missing.
              </figcaption>
            </figure>
          </div>

          <div className="legend">
            <div>
              <svg width="30" height="10" role="img" aria-label="solid line">
                <line className="eg" x1="1" y1="5" x2="21" y2="5" />
                <polygon points="21,1 29,5 21,9" style={{ fill: 'var(--ink-3)' }} />
              </svg>
              NOT NULL — parent row is mandatory
            </div>
            <div>
              <svg width="30" height="10" role="img" aria-label="dashed line">
                <line className="eg-opt" x1="1" y1="5" x2="21" y2="5" />
                <polygon points="21,1 29,5 21,9" style={{ fill: 'var(--ink-3)' }} />
              </svg>
              Nullable — orphan tolerable
            </div>
            <div>
              <svg width="16" height="12" role="img" aria-label="accent box">
                <rect className="nd nd-plan" x="1" y="1" width="14" height="10" rx="2" />
              </svg>
              In the plan
            </div>
            <div>
              <svg width="16" height="12" role="img" aria-label="plain box">
                <rect className="nd" x="1" y="1" width="14" height="10" rx="2" />
              </svg>
              Untouched
            </div>
          </div>

          {map.notes.length > 0 && (
            <div style={{ padding: '14px 16px', borderTop: '1px solid var(--line)' }}>
              <div className="warnline info">
                <span className="g">i</span>
                <ul style={{ margin: 0, paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: 4 }}>
                  {map.notes.map((note, index) => (
                    <li key={index}>{note}</li>
                  ))}
                </ul>
              </div>
            </div>
          )}
        </>
      )}

      <div className="actionbar">
        <span className="guard">
          <span className={`g ${map && map.cycles.length > 0 ? 'del' : 'add'}`}>
            {map && map.cycles.length > 0 ? '!' : '✓'}
          </span>
          {map && map.cycles.length > 0
            ? `${map.cycles.length} table(s) in an FK cycle`
            : 'No FK cycles in this schema'}
        </span>
      </div>
    </div>
  )
}
