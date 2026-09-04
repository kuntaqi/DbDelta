import { useState } from 'react'
import { applyApi, type ApplyResponse, type CompareResponse, type ScriptResponse } from './api'

const OUTCOME_TONE: Record<ApplyResponse['outcome'], string> = {
  Committed: 'ok',
  RolledBack: '',
  Blocked: 'warn',
  Drifted: 'warn',
}

export function PlanScreen({
  comparison,
  script,
  onApplied,
  onBack,
}: {
  comparison: CompareResponse
  script: ScriptResponse
  onApplied: () => void
  onBack: () => void
}) {
  const [confirmation, setConfirmation] = useState('')
  const [allowDestructive, setAllowDestructive] = useState(false)
  const [result, setResult] = useState<ApplyResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // The step says whether it is destructive; reading its SQL for the word DROP used to count a staging
  // table being cleaned up as data loss.
  const destructive = script.steps.filter((s) => s.destructive)
  const confirmed = confirmation === comparison.targetDatabase
  const ready = confirmed && (destructive.length === 0 || allowDestructive) && !comparison.targetReadOnly

  // Built on the server rather than from the text on screen: when rows are staged the download is a zip
  // of the script and its data files, and the browser only has the script half.
  function download() {
    window.location.href = `/api/compare/${comparison.id}/script/download`
  }

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      const response = await applyApi.apply(comparison.id, confirmation, allowDestructive)
      setResult(response)
      if (response.outcome === 'Committed') onApplied()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="app">
      <div className="app-bar">
        <span className="mono">sync plan</span>
        <span className="dim">&rarr;</span>
        <span className="mono">{comparison.targetDatabase}</span>
        <span className="dim mono push">FK-dependency ordered</span>
      </div>

      <div className="split">
        <div className="pane-l">
          <div className="pane-hd">
            Plan <span className="plain rt">{script.stepCount} steps</span>
          </div>
          <ul className="tree" style={{ fontSize: 13 }}>
            {script.steps.map((step, index) => (
              <li key={index} className="it" style={{ cursor: 'default', paddingLeft: 14 }}>
                <span className="nm">{step.description}</span>
                <span className="why dim">{step.phase}</span>
              </li>
            ))}
          </ul>
        </div>

        <div>
          <div className="pane-hd">
            Generated script
            <span className="plain rt">
              {script.stepCount} steps &middot; {(script.byteSize / 1024).toFixed(1)} kB
              {/* Computed since the beginning and never shown, so a script past the reviewable limit read
                  exactly like one under it. The data screen estimates this before the build; this is the
                  measured answer. */}
              {script.exceedsReviewableSize && (
                <>
                  {' '}
                  <span className="badge prod">too large to review here</span>
                </>
              )}
            </span>
          </div>
          <div className="codewrap" style={{ maxHeight: 420, overflowY: 'auto' }}>
            <pre className="code">
              {script.sql.split('\n').map((line, index) => (
                <div className="cl" key={index}>
                  <span className="ln">{index + 1}</span>
                  <span className="cd">{line || ' '}</span>
                </div>
              ))}
            </pre>
          </div>

          <div style={{ padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: 12 }}>
            {comparison.targetReadOnly && (
              <div className="warnline">
                <span className="g">!</span>
                <div>
                  This target is on the read-only list. Download the script and run it yourself &mdash; apply
                  stays disabled.
                </div>
              </div>
            )}

            {script.unsatisfiable.map((message) => (
              <div className="warnline warn" key={message}>
                <span className="g">!</span>
                <div>
                  {message} This script will fail on apply and roll back — compare again with the source
                  that has it, or drop the object that needs it from the plan.
                </div>
              </div>
            ))}

            {script.notEmitted.map((message) => (
              <div className="warnline warn" key={message}>
                <span className="g">!</span>
                <div>
                  {message} The rest of this script still applies — the target will simply not have it, so
                  the next comparison will keep reporting it.
                </div>
              </div>
            ))}

            {script.narrowings.length > 0 && (
              <div className="warnline info">
                <span className="g same">◇</span>
                <div>
                  <b>Some tables are in for particular rows, not all of them.</b> The count that matters is
                  the one nobody picked, so it is repeated every time this script is built.
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                    {script.narrowings.map((note) => (
                      <li key={note}>{note}</li>
                    ))}
                  </ul>
                </div>
              </div>
            )}

            {script.excluded.length > 0 && (
              <div className="warnline">
                <span className="g del">&minus;</span>
                <div>
                  <b>{script.excluded.length} change(s) are deliberately not here.</b> They were left out
                  under a database-wide plan and stayed out.
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                    {script.excluded.map((item) => (
                      <li key={item.id}>
                        <span className="mono">{item.reason}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            )}

            {script.required.length > 0 && (
              <div className="warnline info">
                <span className="g add">+</span>
                <div>
                  <b>{script.required.length} object(s) here were not ticked.</b> They are in the script
                  because something ticked needs them present first.
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                    {script.required.map((item) => (
                      <li key={item.id}>
                        <span className="mono">{item.qualifiedName}</span> &mdash; {item.reason}
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            )}

            {script.requiredRows.length > 0 && (
              <div className="warnline info">
                <span className="g add">+</span>
                <div>
                  <b>
                    {script.requiredRows.reduce((sum, r) => sum + r.rowCount, 0)} row(s) were pulled in as
                    parents.
                  </b>{' '}
                  A seeded row whose parent is not on the target fails on the key, so the parents go in
                  first.
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                    {script.requiredRows.map((item, index) => (
                      <li key={`${item.table}-${item.foreignKeyName}-${index}`}>
                        {item.rowCount} row(s) of <span className="mono">{item.table}</span> &mdash;{' '}
                        <span className="mono">{item.requiredBy}</span> references them through{' '}
                        <span className="mono">{item.foreignKeyName}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            )}

            {script.staged.length > 0 && (
              <div className="warnline info">
                <span className="g add">&#8681;</span>
                <div>
                  <b>
                    {script.staged.reduce((sum, s) => sum + s.rowCount, 0)} row(s) travel beside this script,
                    not inside it.
                  </b>{' '}
                  Too many to read as <span className="mono">INSERT</span> statements, so they load into a
                  staging table and one statement moves them across. Apply streams them over its own
                  connection; the download is a zip of the script and its data files.
                  <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                    {script.staged.map((item) => (
                      <li key={item.dataFileName}>
                        {item.rowCount} row(s) of <span className="mono">{item.table}</span> &mdash;{' '}
                        <span className="mono">{item.dataFileName}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              </div>
            )}

            {script.exceedsReviewableSize && (
              <div className="warnline warn">
                <span className="g">!</span>
                <div>
                  This script is still over the reviewable size even with rows staged out of it, so the bulk
                  is structure rather than data. Read it in a text editor rather than SSMS.
                </div>
              </div>
            )}

            {destructive.length > 0 && (
              <div className="warnline warn">
                <span className="g">!</span>
                <div>
                  <b>{destructive.length} step(s) drop objects or columns.</b> Dropped data does not come back
                  with a rollback of a later run.
                  <ul style={{ margin: '6px 0 8px', paddingLeft: 18 }}>
                    {destructive.map((step, index) => (
                      <li key={index}>{step.description}</li>
                    ))}
                  </ul>
                  <label style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                    <input
                      type="checkbox"
                      checked={allowDestructive}
                      onChange={(e) => setAllowDestructive(e.target.checked)}
                    />
                    I have read these and want them applied
                  </label>
                </div>
              </div>
            )}

            <div>
              <label className="lbl" htmlFor="confirm">
                Type the target database name to enable apply
              </label>
              <input
                id="confirm"
                className="field"
                placeholder={comparison.targetDatabase}
                value={confirmation}
                onChange={(e) => setConfirmation(e.target.value)}
                style={{ maxWidth: 320 }}
              />
            </div>

            {error && (
              <div className="warnline">
                <span className="g">!</span>
                <div>{error}</div>
              </div>
            )}

            {result && (
              <div className={`warnline ${OUTCOME_TONE[result.outcome]}`}>
                <span className="g">{result.outcome === 'Committed' ? '✓' : '!'}</span>
                <div>
                  <b>{result.outcome}</b> &mdash; {result.message}
                  {result.blockers.length > 0 && (
                    <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                      {result.blockers.map((blocker, index) => (
                        <li key={index}>{blocker}</li>
                      ))}
                    </ul>
                  )}
                  {result.serverMessage && (
                    <div className="mono" style={{ marginTop: 6 }}>
                      {result.errorNumber ? `Msg ${result.errorNumber} · ` : ''}
                      {result.serverMessage}
                    </div>
                  )}
                </div>
              </div>
            )}
          </div>

          <div className="actionbar">
            <span className="guard">
              <span className="g add">&#10003;</span>Single transaction &middot; rolls back whole
            </span>
            <span className="guard">
              <span className={`g ${destructive.length > 0 ? 'chg' : 'add'}`}>
                {destructive.length > 0 ? '!' : '✓'}
              </span>
              {destructive.length} destructive step(s)
            </span>
            <span className="push" />
            <button type="button" className="btn" onClick={onBack}>
              Back
            </button>
            <button type="button" className="btn primary" onClick={download}>
              {script.staged.length > 0 ? 'Download .zip' : 'Download .sql'}
            </button>
            <button type="button" className="btn" disabled={!ready || busy} onClick={apply}>
              {busy ? 'Applying…' : `Apply to ${comparison.targetDatabase}`}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
