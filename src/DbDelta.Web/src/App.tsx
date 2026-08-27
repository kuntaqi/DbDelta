import { useState } from 'react'
import {
  api,
  environmentName,
  type CompareResponse,
  type ConnectionRequest,
  type EnvironmentClass,
  type ObjectDetail,
  type ObjectSummary,
  type ProbeResponse,
  type RequiredObject,
  schemaApi,
  type SchemaSelectionResponse,
  type ScriptResponse,
} from './api'
import { PlanScreen } from './PlanScreen'
import { RunsScreen } from './RunsScreen'
import { DataScreen } from './DataScreen'
import { FkMapScreen } from './FkMapScreen'

type Screen = 'connections' | 'schema' | 'data' | 'plan' | 'runs' | 'fk'

const NEEDS_COMPARE = 'Compare two connections first — every screen here reads from that comparison.'

const KIND_GLYPH: Record<ObjectSummary['kind'], { glyph: string; tone: string }> = {
  SourceOnly: { glyph: '+', tone: 'add' },
  TargetOnly: { glyph: '−', tone: 'del' },
  Different: { glyph: '~', tone: 'chg' },
  Same: { glyph: '◇', tone: 'same' },
}

// Object types arrive as PascalCase enum names; splitting them keeps "CheckConstraint" from
// reading as "checkconstraint" in the difference list.
function spaced(type: string): string {
  return type.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()
}

type DiffRow = {
  key: string
  kind: ObjectSummary['kind']
  name: string
  what: string
  source: string | null
  target: string | null
}

// One table instead of two. The old screen listed what differed and then, separately, a table of
// property values — so reading "Segment differs" and reading "NVARCHAR(40) vs NVARCHAR(20)" were two
// lookups. Each difference is one row carrying its own values.
function diffRows(detail: ObjectDetail): DiffRow[] {
  const bodyIsShown = detail.sourceDefinition !== null || detail.targetDefinition !== null

  const rows: DiffRow[] = detail.properties
    // "Definition" carries the whole body of a view or routine. It is already rendered side by side
    // below, and repeating it inside a table cell buries every other row under it.
    .filter((property) => !(property.property === 'Definition' && bodyIsShown))
    .map((property) => ({
      key: `self.${property.property}`,
      kind: detail.summary.kind,
      name: spaced(property.property),
      what: spaced(detail.summary.type),
      source: property.source,
      target: property.target,
    }))

  for (const child of detail.children.filter((c) => c.kind !== 'Same')) {
    const prefix = `${child.name}.`
    const owned = detail.childProperties.filter((p) => p.property.startsWith(prefix))

    if (owned.length === 0) {
      // Added or dropped outright: there is no pair of values to show, only which side has it.
      rows.push({
        key: child.id,
        kind: child.kind,
        name: child.name,
        what: spaced(child.type),
        source: child.kind === 'TargetOnly' ? null : 'present',
        target: child.kind === 'SourceOnly' ? null : 'present',
      })
      continue
    }

    for (const property of owned) {
      rows.push({
        key: `${child.id}.${property.property}`,
        kind: child.kind,
        name: child.name,
        what: `${spaced(child.type)} · ${spaced(property.property.slice(prefix.length))}`,
        source: property.source,
        target: property.target,
      })
    }
  }

  return rows
}

function envClass(environment: EnvironmentClass): string {
  return environmentName[environment].toLowerCase()
}

function EnvBadge({ environment }: { environment: EnvironmentClass }) {
  return <span className={`badge ${envClass(environment)}`}>{environmentName[environment]}</span>
}

function ConnectionCard({
  role,
  value,
  probe,
  busy,
  onChange,
  onTest,
}: {
  role: string
  value: ConnectionRequest
  probe: ProbeResponse | null
  busy: boolean
  onChange: (next: ConnectionRequest) => void
  onTest: () => void
}) {
  const environment = probe?.environment ?? 0
  const pasted = value.connectionString !== undefined && value.connectionString !== null
  const sqlLogin = value.authentication === 'SqlLogin'

  return (
    <div className={`conn ${envClass(environment)}`}>
      <span className="band" />
      <div className="conn-in">
        <div className="conn-hd">
          <h3>{role}</h3>
          {probe && <EnvBadge environment={probe.environment} />}
          <span className="dim mono push">{role === 'Source' ? 'read' : probe?.readOnly ? 'blocked' : 'write'}</span>
        </div>

        <div className="row" style={{ gap: 6 }}>
          <button
            type="button"
            className={`chip ${pasted ? '' : 'on'}`}
            onClick={() => onChange({ ...value, connectionString: null })}
          >
            Enter details
          </button>
          <button
            type="button"
            className={`chip ${pasted ? 'on' : ''}`}
            onClick={() => onChange({ ...value, connectionString: value.connectionString ?? '' })}
          >
            Connection string
          </button>
        </div>

        {pasted ? (
          <div>
            <label className="lbl" htmlFor={`${role}-cs`}>Connection string</label>
            <textarea
              id={`${role}-cs`}
              className="field"
              rows={4}
              spellCheck={false}
              placeholder="Server=HOST,1433;Database=MyDb;Integrated Security=true;TrustServerCertificate=true"
              value={value.connectionString ?? ''}
              onChange={(e) => onChange({ ...value, connectionString: e.target.value })}
              style={{ resize: 'vertical', fontFamily: 'var(--mono)' }}
            />
            <p className="dim" style={{ margin: '6px 0 0', fontSize: 11.5 }}>
              Used as given. The server name is still read back out of it, so a read-only server stays
              blocked either way.
            </p>
          </div>
        ) : (
          <>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 90px', gap: 10 }}>
              <div>
                <label className="lbl" htmlFor={`${role}-server`}>Server</label>
                <input
                  id={`${role}-server`}
                  className="field"
                  value={value.server ?? ''}
                  onChange={(e) => onChange({ ...value, server: e.target.value })}
                />
              </div>
              <div>
                <label className="lbl" htmlFor={`${role}-port`}>Port</label>
                <input
                  id={`${role}-port`}
                  className="field"
                  type="number"
                  placeholder="1433"
                  value={value.port ?? ''}
                  onChange={(e) => onChange({ ...value, port: e.target.value ? Number(e.target.value) : null })}
                />
              </div>
            </div>
            <div>
              <label className="lbl" htmlFor={`${role}-database`}>Database</label>
              <input
                id={`${role}-database`}
                className="field"
                value={value.database ?? ''}
                onChange={(e) => onChange({ ...value, database: e.target.value })}
              />
            </div>
            <div>
              <span className="lbl">Authentication</span>
              <div className="row" style={{ gap: 6 }}>
                <button
                  type="button"
                  className={`chip ${sqlLogin ? '' : 'on'}`}
                  onClick={() => onChange({ ...value, authentication: 'Windows' })}
                >
                  Windows
                </button>
                <button
                  type="button"
                  className={`chip ${sqlLogin ? 'on' : ''}`}
                  onClick={() => onChange({ ...value, authentication: 'SqlLogin' })}
                >
                  SQL login
                </button>
              </div>
            </div>
            {sqlLogin && (
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                <div>
                  <label className="lbl" htmlFor={`${role}-user`}>Login</label>
                  <input
                    id={`${role}-user`}
                    className="field"
                    autoComplete="off"
                    value={value.username ?? ''}
                    onChange={(e) => onChange({ ...value, username: e.target.value })}
                  />
                </div>
                <div>
                  <label className="lbl" htmlFor={`${role}-pass`}>Password</label>
                  <input
                    id={`${role}-pass`}
                    className="field"
                    type="password"
                    autoComplete="off"
                    value={value.password ?? ''}
                    onChange={(e) => onChange({ ...value, password: e.target.value })}
                  />
                </div>
              </div>
            )}
            <label className="dim" style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 12 }}>
              <input
                type="checkbox"
                checked={value.trustServerCertificate ?? true}
                onChange={(e) => onChange({ ...value, trustServerCertificate: e.target.checked })}
              />
              Trust the server certificate (needed for most internal servers)
            </label>
          </>
        )}

        <div className="row">
          <button type="button" className="btn" onClick={onTest} disabled={busy}>
            Test connection
          </button>
          {probe && (
            <span className="guard">
              <span className="g add">&#10003;</span>
              {probe.tableCount} tables &middot; {probe.viewCount} views
            </span>
          )}
        </div>
        {probe && (
          <div className="dim" style={{ fontSize: 11.5 }}>
            {probe.edition} &middot; {probe.productVersion} &middot; {probe.collation}
          </div>
        )}
        {probe?.warnings.map((warning) => (
          <div className="warnline warn" key={warning}>
            <span className="g">!</span>
            <div>{warning}</div>
          </div>
        ))}
        {probe?.readOnly && (
          <div className="warnline">
            <span className="g">!</span>
            <div>
              <b>{probe.server}</b> is on the read-only list. Compare and download work; apply stays disabled.
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

function DiffLines({ text, other }: { text: string | null; other: string | null }) {
  const lines = (text ?? '').split('\n')
  const otherLines = new Set((other ?? '').split('\n').map((l) => l.trim()))
  return (
    <div className="codewrap">
      <pre className="code">
        {lines.map((line, index) => {
          const changed = line.trim().length > 0 && !otherLines.has(line.trim())
          return (
            <div className={`cl ${changed ? 'chg' : ''}`} key={index}>
              <span className="ln">{index + 1}</span>
              <span className="cd">{line || ' '}</span>
            </div>
          )
        })}
      </pre>
    </div>
  )
}

export default function App() {
  const [source, setSource] = useState<ConnectionRequest>({
    server: '(localdb)\\MSSQLLocalDB',
    database: 'DbDelta_Demo_Src',
  })
  const [target, setTarget] = useState<ConnectionRequest>({
    server: '(localdb)\\MSSQLLocalDB',
    database: 'DbDelta_Demo_Tgt',
  })
  const [sourceProbe, setSourceProbe] = useState<ProbeResponse | null>(null)
  const [targetProbe, setTargetProbe] = useState<ProbeResponse | null>(null)
  const [comparison, setComparison] = useState<CompareResponse | null>(null)
  const [detail, setDetail] = useState<ObjectDetail | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [script, setScript] = useState<ScriptResponse | null>(null)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [required, setRequired] = useState<RequiredObject[]>([])
  const [unsatisfiable, setUnsatisfiable] = useState<string[]>([])
  const [screen, setScreen] = useState<Screen>('connections')
  const [showSame, setShowSame] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Prerequisites come back with every selection change rather than being tracked here, because the
  // server recomputes them from the ticks: unticking the only object that needed one drops it again.
  function applySelection(value: SchemaSelectionResponse) {
    setPicked(new Set(value.selected))
    setRequired(value.required)
    setUnsatisfiable(value.unsatisfiable)
  }

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

  // One object is open at a time, and its detail is fetched when it opens rather than with the
  // comparison: the emitter runs per object, so fetching all of them up front would be work nobody
  // asked for on a database with 263 tables.
  function toggleObject(object: ObjectSummary) {
    if (expanded === object.id) {
      setExpanded(null)
      setDetail(null)
      return
    }

    if (!comparison) {
      return
    }

    run(
      () => api.detail(comparison.id, object.id),
      (value) => {
        setDetail(value)
        setExpanded(object.id)
      },
    )
  }

  const requiredById = new Map(required.map((r) => [r.id, r]))
  const visible = comparison?.objects.filter((o) => showSame || o.kind !== 'Same') ?? []
  const grouped = visible.reduce<Record<string, ObjectSummary[]>>((acc, object) => {
    ;(acc[object.type] ??= []).push(object)
    return acc
  }, {})

  return (
    <>
      <div className="topbar">
        <div className="topbar-in">
          <span className="brand">
            <span className="brand-mark">&#916;</span>DbDelta
          </span>
          {/* Not a wizard. A compared pair of connections is the only prerequisite, and every screen it
              enables stays reachable from every other one — numbering them implied an order that the tool
              never actually enforced. A disabled tab says what is missing rather than leaving it a mystery. */}
          <nav className="steps">
            <button type="button" className={`step ${screen === 'connections' ? 'on' : ''}`} onClick={() => setScreen('connections')}>
              Connections
            </button>
            <button
              type="button"
              className={`step ${screen === 'schema' ? 'on' : ''}`}
              onClick={() => setScreen('schema')}
              disabled={!comparison}
              title={comparison ? undefined : NEEDS_COMPARE}
            >
              Schema compare
            </button>
            <button
              type="button"
              className={`step ${screen === 'data' ? 'on' : ''}`}
              onClick={() => setScreen('data')}
              disabled={!comparison}
              title={comparison ? undefined : NEEDS_COMPARE}
            >
              Data compare
            </button>
            <button
              type="button"
              className={`step ${screen === 'plan' ? 'on' : ''}`}
              onClick={() =>
                comparison &&
                run(
                  () => api.script(comparison.id),
                  (value) => {
                    setScript(value)
                    setScreen('plan')
                  },
                )
              }
              disabled={!comparison}
              title={comparison ? undefined : NEEDS_COMPARE}
            >
              Sync plan
            </button>
            <button type="button" className={`step ${screen === 'runs' ? 'on' : ''}`} onClick={() => setScreen('runs')}>
              Run log
            </button>
            <button
              type="button"
              className={`step ${screen === 'fk' ? 'on' : ''}`}
              onClick={() => setScreen('fk')}
              disabled={!comparison}
              title={comparison ? undefined : NEEDS_COMPARE}
            >
              FK map
            </button>
          </nav>
        </div>
      </div>

      <div className="wrap">
        {error && (
          <div className="warnline" style={{ marginBottom: 14 }}>
            <span className="g">!</span>
            <div>{error}</div>
          </div>
        )}

        {screen === 'connections' && (
          <div className="app">
            <div className="app-bar">
              <span className="mono">connections</span>
              {busy && <span className="spinner push">working&hellip;</span>}
            </div>
            <div className="app-body">
              <div className="conns">
                <ConnectionCard
                  role="Source"
                  value={source}
                  probe={sourceProbe}
                  busy={busy}
                  onChange={setSource}
                  onTest={() => run(() => api.probe(source), setSourceProbe)}
                />
                <div className="arrowcol">
                  <span className="mono dim" style={{ fontSize: 20 }}>&rarr;</span>
                  <button
                    type="button"
                    className="btn"
                    title="Swap source and target"
                    style={{ padding: '5px 8px' }}
                    onClick={() => {
                      setSource(target)
                      setTarget(source)
                      setSourceProbe(targetProbe)
                      setTargetProbe(sourceProbe)
                    }}
                  >
                    &#8646;
                  </button>
                </div>
                <ConnectionCard
                  role="Target"
                  value={target}
                  probe={targetProbe}
                  busy={busy}
                  onChange={setTarget}
                  onTest={() => run(() => api.probe(target), setTargetProbe)}
                />
              </div>

              <div className="warnline info" style={{ marginTop: 16 }}>
                <span className="g">&rarr;</span>
                <div>
                  Changes would be written to the <b>target</b> connection. Nothing is ever written to the source.
                </div>
              </div>
            </div>
            <div className="actionbar">
              <span className="guard">Schema compare</span>
              <span className="push" />
              <button
                type="button"
                className="btn primary"
                disabled={busy}
                onClick={() =>
                  run(
                    () => api.compare(source, target),
                    (result) => {
                      setComparison(result)
                      // A new comparison is a new session on the server, so anything derived from the
                      // previous one goes with it. Leaving these behind showed ticks and a script that
                      // belonged to a comparison that no longer exists.
                      setDetail(null)
                      setExpanded(null)
                      setPicked(new Set())
                      setRequired([])
                      setUnsatisfiable([])
                      setScript(null)
                      setScreen('schema')
                    },
                  )
                }
              >
                Compare
              </button>
            </div>
          </div>
        )}

        {screen === 'schema' && comparison && (
          <div className="app">
            <div className="app-bar">
              <span className="mono">{comparison.sourceDatabase}</span>
              <EnvBadge environment={comparison.sourceEnvironment} />
              <span className="dim">&rarr;</span>
              <span className="mono">{comparison.targetDatabase}</span>
              <EnvBadge environment={comparison.targetEnvironment} />
              <span className="dim mono push">compared in {comparison.durationMs} ms</span>
            </div>

            <div className="app-body" style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              {comparison.warnings.map((warning) => (
                <div className="warnline warn" key={warning}>
                  <span className="g">!</span>
                  <div>{warning}</div>
                </div>
              ))}
              {comparison.targetIsEmpty && (
                <div className="warnline info">
                  <span className="g">i</span>
                  <div>
                    The target holds no user objects, so this is a provisioning run rather than a diff. Data stays
                    off until you opt in per table.
                  </div>
                </div>
              )}

              {/* The plan can hold more than was clicked, so the difference is stated rather than left to
                  be noticed on the script screen. */}
              {required.length > 0 && (
                <div className="warnline info">
                  <span className="g add">+</span>
                  <div>
                    <b>
                      {required.length} object(s) are in the plan because something you ticked needs them.
                    </b>{' '}
                    They go in ahead of what required them.
                    <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                      {required.map((item) => (
                        <li key={item.id}>
                          <span className="mono">{item.qualifiedName}</span> &mdash; {item.reason}
                        </li>
                      ))}
                    </ul>
                  </div>
                </div>
              )}

              {unsatisfiable.map((message) => (
                <div className="warnline warn" key={message}>
                  <span className="g">!</span>
                  <div>{message}</div>
                </div>
              ))}

              <div className="tiles">
                {comparison.counts.map((count) => (
                  <div className={`tile ${count.changed > 0 ? 'chg' : ''}`} key={count.type}>
                    <div className="k">{count.type}</div>
                    <div className="v">{count.changed}</div>
                    <div className="s">of {count.total} differ</div>
                  </div>
                ))}
              </div>

              <div className="row" style={{ flexWrap: 'wrap' }}>
                <button type="button" className={`chip ${showSame ? 'on' : ''}`} onClick={() => setShowSame(!showSame)}>
                  Show identical <span className="n">{comparison.objects.filter((o) => o.kind === 'Same').length}</span>
                </button>
                <span className="dim" style={{ fontSize: 12 }}>
                  {comparison.objects.filter((o) => o.kind !== 'Same').length} object(s) differ
                </span>
                <span className="push" />
                <button
                  type="button"
                  className="chip"
                  onClick={() =>
                    run(
                      () => schemaApi.selectAll(comparison.id, true),
                      (value) => applySelection(value),
                    )
                  }
                >
                  Select all differing
                </button>
                <button
                  type="button"
                  className="chip"
                  disabled={picked.size === 0}
                  onClick={() =>
                    run(
                      () => schemaApi.selectAll(comparison.id, false),
                      (value) => applySelection(value),
                    )
                  }
                >
                  Clear
                </button>
              </div>
            </div>

            <ul className="tree">
              {Object.entries(grouped).map(([type, objects]) => (
                <li key={type}>
                  <div className="grp">
                    {type}
                    <span className="cnt">{objects.length}</span>
                  </div>
                  <ul style={{ margin: 0, padding: 0, listStyle: 'none' }}>
                    {objects.map((object) => (
                      <li key={object.id}>
                        {/* Ticking for the plan and opening the difference are separate acts: the whole row
                            opens it in place, the checkbox only ever ticks. The row expands where it sits,
                            so nothing about where you are in the list is lost. */}
                        <div
                          className={`it ${expanded === object.id ? 'on' : ''}`}
                          style={{ paddingLeft: 14 }}
                          onClick={() => toggleObject(object)}
                        >
                          {object.kind === 'Same' ? (
                            <span className="cb" aria-hidden="true" />
                          ) : (
                            <input
                              type="checkbox"
                              checked={picked.has(object.id)}
                              onClick={(e) => e.stopPropagation()}
                              onChange={(e) =>
                                run(
                                  () => schemaApi.select(comparison.id, object.id, e.target.checked),
                                  (value) => applySelection(value),
                                )
                              }
                              aria-label={`Include ${object.qualifiedName} in the sync plan`}
                            />
                          )}
                          <span className={`g ${KIND_GLYPH[object.kind].tone}`}>{KIND_GLYPH[object.kind].glyph}</span>
                          <button
                            type="button"
                            className="linkish"
                            aria-expanded={expanded === object.id}
                            onClick={(e) => {
                              e.stopPropagation()
                              toggleObject(object)
                            }}
                          >
                            <span className="nm">{object.qualifiedName}</span>
                          </button>
                          <span className="why">{object.summary}</span>
                          {/* An unticked row that is in the plan anyway has to say so where the tick is,
                              not only in the summary above the list. */}
                          {requiredById.has(object.id) && !picked.has(object.id) && (
                            <span
                              className="chip on"
                              style={{ marginLeft: 8, fontSize: 11 }}
                              title={requiredById.get(object.id)!.reason}
                            >
                              required by {requiredById.get(object.id)!.requiredBy}
                            </span>
                          )}
                          <span className="dim mono" style={{ fontSize: 11, marginLeft: 8 }}>
                            {expanded === object.id ? '▾' : '▸'}
                          </span>
                        </div>

                        {expanded === object.id && detail && (
                          <div className="objdetail">
                            {diffRows(detail).length === 0 ? (
                              <div className="pane-hd plain" style={{ textTransform: 'none' }}>
                                No property-level differences — the body text below is what differs.
                              </div>
                            ) : (
                              <table className="grid">
                                <thead>
                                  <tr>
                                    <th style={{ width: '44%' }}>What differs</th>
                                    <th>Source</th>
                                    <th>Target</th>
                                  </tr>
                                </thead>
                                <tbody>
                                  {diffRows(detail).map((row) => (
                                    <tr key={row.key}>
                                      <td>
                                        <span className={`g ${KIND_GLYPH[row.kind].tone}`}>
                                          {KIND_GLYPH[row.kind].glyph}
                                        </span>
                                        <span className="nm">{row.name}</span>
                                        <span className="why dim">{row.what}</span>
                                      </td>
                                      <td style={{ color: row.source ? 'var(--add)' : undefined }}>
                                        {row.source ?? <span className="dim">&mdash;</span>}
                                      </td>
                                      <td style={{ color: row.target ? 'var(--del)' : undefined }}>
                                        {row.target ?? <span className="dim">&mdash;</span>}
                                      </td>
                                    </tr>
                                  ))}
                                </tbody>
                              </table>
                            )}

                            <div className="pane-hd" style={{ borderTop: '1px solid var(--line)' }}>
                              <span className="plain">Source &middot; {comparison.sourceDatabase}</span>
                              <span className="plain rt">Target &middot; {comparison.targetDatabase}</span>
                            </div>
                            <div className="two">
                              <DiffLines text={detail.sourceDefinition} other={detail.targetDefinition} />
                              <DiffLines text={detail.targetDefinition} other={detail.sourceDefinition} />
                            </div>

                            <div className="pane-hd" style={{ borderTop: '1px solid var(--line)' }}>
                              Will run on target
                              <span className="plain rt">
                                {detail.plannedStatements.length} statement(s)
                              </span>
                            </div>
                            <div className="codewrap" style={{ padding: '10px 0 14px' }}>
                              <pre className="code">
                                {detail.plannedStatements.length === 0 ? (
                                  <div className="cl">
                                    <span className="ln">1</span>
                                    <span className="cd dim">-- nothing to run for this object</span>
                                  </div>
                                ) : (
                                  detail.plannedStatements.map((statement, index) => (
                                    <div className="cl" key={index}>
                                      <span className="ln">{index + 1}</span>
                                      <span className="cd">{statement}</span>
                                    </div>
                                  ))
                                )}
                              </pre>
                            </div>
                          </div>
                        )}
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>

            <div className="actionbar">
              <span className="guard">
                <span className={`g ${picked.size > 0 ? 'add' : 'same'}`}>{picked.size > 0 ? '✓' : '◇'}</span>
                {/* The plan is the ticks plus what they require, so counting only the ticks would
                    understate what is about to run. */}
                {picked.size === 0
                  ? 'Nothing picked. Tick the objects you want in the plan — none are selected for you.'
                  : required.length === 0
                    ? `${picked.size} object(s) in the plan`
                    : `${picked.size + required.length} object(s) in the plan — ${picked.size} ticked, ${required.length} required`}
              </span>
              {expanded && detail && (
                <span className="dim" style={{ fontSize: 11.5 }}>
                  &middot; {detail.plannedStatements.length} statement(s) for {detail.summary.qualifiedName}
                </span>
              )}
              <span className="push" />
              <button type="button" className="btn" onClick={() => setScreen('connections')}>
                Back to connections
              </button>
            </div>
          </div>
        )}
        {screen === 'data' && comparison && <DataScreen comparison={comparison} />}

        {screen === 'plan' && comparison && script && (
          <PlanScreen
            comparison={comparison}
            script={script}
            onApplied={() => setScreen('runs')}
            onBack={() => setScreen('schema')}
          />
        )}

        {screen === 'runs' && <RunsScreen />}

        {screen === 'fk' && comparison && <FkMapScreen comparison={comparison} />}

      </div>
    </>
  )
}
