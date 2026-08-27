import { useState } from 'react'
import {
  formatBytes,
  instanceApi,
  type ConnectionRequest,
  type InstanceSurveyResponse,
  environmentName,
} from './api'

// Every other screen starts from a compared pair, so until now there was no way to ask what is on an
// instance without already knowing which two databases to compare. This one starts from a server.
export function InstanceScreen({
  source,
  target,
  onUseAsSource,
  onUseAsTarget,
}: {
  source: ConnectionRequest
  target: ConnectionRequest
  onUseAsSource: (database: string) => void
  onUseAsTarget: (database: string) => void
}) {
  const [side, setSide] = useState<'Source' | 'Target'>('Source')
  const [survey, setSurvey] = useState<InstanceSurveyResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const connection = side === 'Source' ? source : target

  async function load(describe: string[] | null) {
    setBusy(true)
    setError(null)
    try {
      setSurvey(
        describe === null
          ? await instanceApi.survey(connection)
          : await instanceApi.describe(connection, describe),
      )
    } catch (e) {
      setSurvey(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const described = survey?.databases.filter((d) => d.tables !== null).length ?? 0
  const collations = [...new Set(survey?.databases.map((d) => d.collation).filter(Boolean) ?? [])]

  return (
    <div className="app">
      <div className="app-bar">
        <span className="mono">instance</span>
        {survey && (
          <>
            <span className="dim">&middot;</span>
            <span className="mono">{survey.server}</span>
            <span className={`badge ${environmentName[survey.environment].toLowerCase()}`}>
              {environmentName[survey.environment]}
            </span>
            {survey.readOnly && <span className="badge prod">read-only</span>}
          </>
        )}
        <span className="dim mono push">
          {survey ? `${survey.databases.length} user databases` : 'not surveyed yet'}
        </span>
      </div>

      <div className="scopebar">
        <span className="lbl" style={{ margin: 0 }}>Connection</span>
        {(['Source', 'Target'] as const).map((value) => (
          <button
            key={value}
            type="button"
            className={`chip ${side === value ? 'on' : ''}`}
            onClick={() => {
              setSide(value)
              setSurvey(null)
            }}
          >
            {value}
          </button>
        ))}
        <button type="button" className="btn" disabled={busy} onClick={() => load(null)}>
          {busy ? 'Working…' : 'Survey the instance'}
        </button>
        {survey && (
          <button type="button" className="chip" disabled={busy} onClick={() => load([])}>
            Read collation and object counts
          </button>
        )}
      </div>

      <div className="app-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        <p className="dim" style={{ margin: 0, fontSize: 11.5 }}>
          The list is one query and answers instantly however many databases there are. Collation and object
          counts need a connection each, so they are a second pass you ask for &mdash; about a tenth of a
          second per database. System databases are left out: none of them is something this tool would sync.
        </p>

        {error && (
          <div className="warnline">
            <span className="g">!</span>
            <div>{error}</div>
          </div>
        )}

        {survey?.warning && (
          <div className="warnline warn">
            <span className="g">!</span>
            <div>{survey.warning}</div>
          </div>
        )}

        {/* The reason to look at a whole instance rather than a pair: a split like this is invisible when
            you can only ever see two databases at a time, and it decides whether a data compare can be
            trusted at all. */}
        {collations.length > 1 && (
          <div className="warnline warn">
            <span className="g">!</span>
            <div>
              <b>{collations.length} different collations on this instance.</b> Comparing a pair that does not
              share one makes string comparison — and so every row hash — differ between the sides.
              <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                {collations.map((c) => (
                  <li key={c}>
                    <span className="mono">{c}</span>
                  </li>
                ))}
              </ul>
            </div>
          </div>
        )}
      </div>

      {survey && (
        <div className="gridwrap">
          <table className="grid">
            <thead>
              <tr>
                <th>Database</th>
                <th>State</th>
                <th style={{ textAlign: 'right' }}>Data</th>
                <th style={{ textAlign: 'right' }}>Log</th>
                <th>Collation</th>
                <th style={{ textAlign: 'right' }}>Tables</th>
                <th style={{ textAlign: 'right' }}>Views</th>
                <th style={{ textAlign: 'right' }}>Routines</th>
                <th>Use</th>
              </tr>
            </thead>
            <tbody>
              {survey.databases.map((database) => (
                <tr key={database.name} className={database.accessible ? undefined : 'rowfail'}>
                  <td className="mono">{database.name}</td>
                  <td>
                    <span className="plain">{database.state.toLowerCase()}</span>
                    {database.isReadOnly && <span className="plain"> &middot; read-only</span>}
                    {!database.accessible && <span className="plain"> &middot; no access</span>}
                  </td>
                  <td className="num">{formatBytes(database.dataBytes)}</td>
                  <td className="num">{formatBytes(database.logBytes)}</td>
                  <td className="mono" style={{ fontSize: 11.5 }}>
                    {database.problem ? (
                      <span className="dim" title={database.problem}>
                        not read
                      </span>
                    ) : (
                      (database.collation ?? <span className="dim">&mdash;</span>)
                    )}
                  </td>
                  <td className="num">{database.tables ?? <span className="dim">&mdash;</span>}</td>
                  <td className="num">{database.views ?? <span className="dim">&mdash;</span>}</td>
                  <td className="num">{database.routines ?? <span className="dim">&mdash;</span>}</td>
                  <td>
                    <div className="row" style={{ gap: 6 }}>
                      <button
                        type="button"
                        className="chip"
                        disabled={!database.accessible}
                        onClick={() => onUseAsSource(database.name)}
                      >
                        source
                      </button>
                      <button
                        type="button"
                        className="chip"
                        disabled={!database.accessible}
                        onClick={() => onUseAsTarget(database.name)}
                      >
                        target
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {survey && (
        <div className="actionbar">
          <span className="guard">
            <span className={`g ${described === survey.databases.length ? 'add' : 'same'}`}>
              {described === survey.databases.length ? '✓' : '◇'}
            </span>
            {described} of {survey.databases.length} read in detail
          </span>
          <span className="push" />
          <span className="dim" style={{ fontSize: 11.5 }}>
            Picking a database here fills in the connection; comparing is still a separate click.
          </span>
        </div>
      )}
    </div>
  )
}
