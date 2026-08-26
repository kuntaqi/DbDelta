import { useEffect, useState } from 'react'
import { applyApi, type RunLogEntryDto } from './api'

const OUTCOME_BADGE: Record<string, string> = {
  Committed: 'dev',
  RolledBack: 'prod',
  Blocked: 'uat',
  Drifted: 'uat',
}

export function RunsScreen() {
  const [runs, setRuns] = useState<RunLogEntryDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    applyApi
      .runs()
      .then(setRuns)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [])

  return (
    <div className="app">
      <div className="app-bar">
        <span className="mono">run log</span>
        <span className="dim mono push">{runs ? `${runs.length} run(s)` : 'loading…'}</span>
      </div>

      {error && (
        <div style={{ padding: 16 }}>
          <div className="warnline">
            <span className="g">!</span>
            <div>{error}</div>
          </div>
        </div>
      )}

      {runs && runs.length === 0 && (
        <div style={{ padding: 20 }} className="dim">
          Nothing applied yet. Runs are written to <span className="mono">%APPDATA%\DbDelta\runs</span> — never
          into any database this tool connects to.
        </div>
      )}

      {runs && runs.length > 0 && (
        <table className="grid">
          <thead>
            <tr>
              <th>When</th>
              <th>Route</th>
              <th>Action</th>
              <th>Steps</th>
              <th>Result</th>
              <th>Took</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((run) => (
              <tr key={run.id}>
                <td style={{ fontFamily: 'var(--sans)' }}>{new Date(run.at).toLocaleString()}</td>
                <td>{run.route}</td>
                <td style={{ fontFamily: 'var(--sans)' }}>{run.action}</td>
                <td style={{ textAlign: 'right' }}>{run.stepCount}</td>
                <td>
                  <span className={`badge ${OUTCOME_BADGE[run.outcome] ?? 'unknown'}`}>{run.outcome}</span>
                  {run.serverMessage && (
                    <div style={{ color: 'var(--del)', fontSize: 11.5, marginTop: 4, maxWidth: 460 }}>
                      {run.serverMessage}
                    </div>
                  )}
                </td>
                <td style={{ textAlign: 'right' }}>{run.durationMs} ms</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="actionbar">
        <span className="guard">Each run keeps the script it used, so a failure can be re-run as it was.</span>
      </div>
    </div>
  )
}
