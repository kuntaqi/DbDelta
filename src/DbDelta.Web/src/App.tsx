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
  type ScriptResponse,
} from './api'
import { PlanScreen } from './PlanScreen'
import { RunsScreen } from './RunsScreen'
import { DataScreen } from './DataScreen'
import { FkMapScreen } from './FkMapScreen'

type Screen = 'connections' | 'overview' | 'detail' | 'data' | 'plan' | 'runs' | 'fk'

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
  const [script, setScript] = useState<ScriptResponse | null>(null)
  const [screen, setScreen] = useState<Screen>('connections')
  const [showSame, setShowSame] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

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
          <nav className="steps">
            <button type="button" className={`step ${screen === 'connections' ? 'on' : ''}`} onClick={() => setScreen('connections')}>
              1 Connections
            </button>
            <button
              type="button"
              className={`step ${screen === 'overview' ? 'on' : ''}`}
              onClick={() => setScreen('overview')}
              disabled={!comparison}
            >
              2 Overview
            </button>
            <button
              type="button"
              className={`step ${screen === 'detail' ? 'on' : ''}`}
              onClick={() => setScreen('detail')}
              disabled={!detail}
            >
              3 Schema detail
            </button>
            <button
              type="button"
              className={`step ${screen === 'data' ? 'on' : ''}`}
              onClick={() => setScreen('data')}
              disabled={!comparison}
            >
              4 Data compare
            </button>
            <button
              type="button"
              className={`step ${screen === 'plan' ? 'on' : ''}`}
              onClick={() =>
                comparison &&
                run(
                  () => api.script(comparison.id, []),
                  (value) => {
                    setScript(value)
                    setScreen('plan')
                  },
                )
              }
              disabled={!comparison}
            >
              5 Sync plan
            </button>
            <button type="button" className={`step ${screen === 'runs' ? 'on' : ''}`} onClick={() => setScreen('runs')}>
              6 Run log
            </button>
            <button
              type="button"
              className={`step ${screen === 'fk' ? 'on' : ''}`}
              onClick={() => setScreen('fk')}
              disabled={!comparison}
            >
              7 FK map
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
                      setDetail(null)
                      setScreen('overview')
                    },
                  )
                }
              >
                Compare
              </button>
            </div>
          </div>
        )}

        {screen === 'overview' && comparison && (
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
                        <button
                          type="button"
                          className={`it ${detail?.summary.id === object.id ? 'on' : ''}`}
                          onClick={() =>
                            run(
                              () => api.detail(comparison.id, object.id),
                              (value) => {
                                setDetail(value)
                                setScreen('detail')
                              },
                            )
                          }
                        >
                          <span className={`g ${KIND_GLYPH[object.kind].tone}`}>{KIND_GLYPH[object.kind].glyph}</span>
                          <span className="nm">{object.qualifiedName}</span>
                          <span className="why">{object.summary}</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                </li>
              ))}
            </ul>

            <div className="actionbar">
              <span className="guard">Pick an object to see what changes</span>
              <span className="push" />
              <button type="button" className="btn" onClick={() => setScreen('connections')}>
                Back to connections
              </button>
            </div>
          </div>
        )}

        {screen === 'detail' && detail && comparison && (
          <div className="app">
            <div className="app-bar">
              <span className={`g ${KIND_GLYPH[detail.summary.kind].tone}`}>
                {KIND_GLYPH[detail.summary.kind].glyph}
              </span>
              <span className="mono">{detail.summary.qualifiedName}</span>
              <span className="badge unknown">{detail.summary.type}</span>
              <span className="dim mono push">{detail.summary.summary}</span>
            </div>

            <div className="split">
              <div className="pane-l">
                <div className="pane-hd">Difference summary</div>
                {detail.children.filter((c) => c.kind !== 'Same').length === 0 && detail.childProperties.length === 0 ? (
                  <div style={{ padding: '12px 14px' }} className="dim">
                    Body differs; see the side-by-side text.
                  </div>
                ) : (
                  <ul className="tree" style={{ fontSize: 13 }}>
                    {detail.children
                      .filter((child) => child.kind !== 'Same')
                      .map((child) => (
                        <li key={child.id} className="it" style={{ cursor: 'default' }}>
                          <span className={`g ${KIND_GLYPH[child.kind].tone}`}>{KIND_GLYPH[child.kind].glyph}</span>
                          <span className="nm">{child.name}</span>
                          <span className="why dim">{spaced(child.type)}</span>
                        </li>
                      ))}
                  </ul>
                )}

                {detail.childProperties.length > 0 && (
                  <>
                    <div className="pane-hd">Values</div>
                    <table className="grid">
                      <thead>
                        <tr>
                          <th>Property</th>
                          <th>Source</th>
                          <th>Target</th>
                        </tr>
                      </thead>
                      <tbody>
                        {detail.childProperties.map((property) => (
                          <tr key={property.property}>
                            <td>{property.property}</td>
                            <td style={{ color: 'var(--add)' }}>{property.source ?? '—'}</td>
                            <td style={{ color: 'var(--del)' }}>{property.target ?? '—'}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </>
                )}
              </div>

              <div>
                <div className="pane-hd">
                  <span className="plain">Source &middot; {comparison.sourceDatabase}</span>
                  <span className="plain rt">Target &middot; {comparison.targetDatabase}</span>
                </div>
                <div className="two">
                  <DiffLines text={detail.sourceDefinition} other={detail.targetDefinition} />
                  <DiffLines text={detail.targetDefinition} other={detail.sourceDefinition} />
                </div>

                <div className="pane-hd" style={{ borderTop: '1px solid var(--line)' }}>
                  Will run on target
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
            </div>

            <div className="actionbar">
              <span className="guard">
                <span className="g same">&#9671;</span>
                {detail.plannedStatements.length} statement(s) for this object
              </span>
              <span className="push" />
              <button type="button" className="btn" onClick={() => setScreen('overview')}>
                Back to overview
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
            onBack={() => setScreen('overview')}
          />
        )}

        {screen === 'runs' && <RunsScreen />}

        {screen === 'fk' && comparison && <FkMapScreen comparison={comparison} />}

      </div>
    </>
  )
}
