import { useState } from 'react'
import {
  formatBytes,
  instanceApi,
  type ConnectionRequest,
  type InstanceSurveyResponse,
  type InstanceComparisonResponse,
  type DatabasePair,
  environmentName,
} from './api'

// A glyph per verdict, and the two that mean "nothing was established" deliberately do not get a tick.
// CountsMatch is the one worth being careful about: equal counts are not equal schemas, so it reads as an
// open question rather than as agreement.
const SIGNAL_GLYPH: Record<DatabasePair['signal'], string> = {
  SourceOnly: '+',
  TargetOnly: '−',
  Unreadable: '?',
  NotCompared: '◇',
  CollationDiffers: '!',
  CountsDiffer: '≠',
  CountsMatch: '◇',
}

const SIGNAL_TONE: Record<DatabasePair['signal'], string> = {
  SourceOnly: 'add',
  TargetOnly: 'del',
  Unreadable: 'same',
  NotCompared: 'same',
  CollationDiffers: 'del',
  CountsDiffer: 'del',
  CountsMatch: 'same',
}

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
  const [side, setSide] = useState<'Source' | 'Target' | 'Both'>('Source')
  const [survey, setSurvey] = useState<InstanceSurveyResponse | null>(null)
  const [comparison, setComparison] = useState<InstanceComparisonResponse | null>(null)
  // Pairs entered as "sourceName targetName" per line, for the case the two servers do not share names.
  const [pairText, setPairText] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const connection = side === 'Target' ? target : source

  // Two names per line, separated by whitespace or an arrow. Anything else on the line is ignored rather
  // than rejected: this is a scratch pad, and refusing a half-typed line while someone types it is noise.
  const pairings = pairText
    .split('\n')
    .map((line) => line.split(/\s*(?:->|=>|\s|,)\s*/).filter(Boolean))
    .filter((parts) => parts.length >= 2)
    .map(([s, t]) => ({ source: s, target: t }))

  async function compare(describe: boolean) {
    setBusy(true)
    setError(null)
    try {
      setComparison(await instanceApi.compare(source, target, pairings, describe))
    } catch (e) {
      setComparison(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

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
        {(['Source', 'Target', 'Both'] as const).map((value) => (
          <button
            key={value}
            type="button"
            className={`chip ${side === value ? 'on' : ''}`}
            onClick={() => {
              setSide(value)
              setSurvey(null)
              setComparison(null)
            }}
          >
            {value === 'Both' ? 'Compare both' : value}
          </button>
        ))}
        {side === 'Both' ? (
          <>
            <button type="button" className="btn" disabled={busy} onClick={() => compare(false)}>
              {busy ? 'Working…' : 'Match the database lists'}
            </button>
            {comparison && !comparison.described && comparison.onBothSides > 0 && (
              <button type="button" className="chip" disabled={busy} onClick={() => compare(true)}>
                Read collation and counts on both sides
              </button>
            )}
          </>
        ) : (
          <>
            <button type="button" className="btn" disabled={busy} onClick={() => load(null)}>
              {busy ? 'Working…' : 'Survey the instance'}
            </button>
            {survey && (
              <button type="button" className="chip" disabled={busy} onClick={() => load([])}>
                Read collation and object counts
              </button>
            )}
          </>
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

        {side === 'Both' && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <span className="lbl" style={{ margin: 0 }}>
              Pair databases whose names differ (one pair per line)
            </span>
            <textarea
              className="mono"
              rows={3}
              spellCheck={false}
              placeholder={'AppProd  AppUat\nBillingProd -> BillingUat'}
              value={pairText}
              onChange={(e) => setPairText(e.target.value)}
              style={{ resize: 'vertical' }}
            />
            {/* The whole reason this box exists. Where the environment is part of the name, the two servers
                have no names in common and matching by name finds nothing at all. */}
            <span className="dim" style={{ fontSize: 11.5 }}>
              Same-named databases pair up on their own. This is for the case where they do not —{' '}
              <span className="mono">AppProd</span> against <span className="mono">AppUat</span> — which
              matching by name cannot see.
              {pairings.length > 0 && ` ${pairings.length} pair(s) will be sent.`}
            </span>
          </div>
        )}

        {comparison?.warnings.map((warning) => (
          <div key={warning} className="warnline warn">
            <span className="g">!</span>
            <div>{warning}</div>
          </div>
        ))}

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

      {comparison && (
        <div className="gridwrap">
          <table className="grid">
            <thead>
              <tr>
                <th style={{ width: 160 }}>{comparison.sourceServer}</th>
                <th style={{ width: 160 }}>{comparison.targetServer}</th>
                <th style={{ width: 84 }}>Matched</th>
                <th>What is known</th>
                <th style={{ width: 92 }} />
              </tr>
            </thead>
            <tbody>
              {comparison.pairs.map((pair) => (
                <tr key={`${pair.kind}-${pair.name}`}>
                  <td className="mono">{pair.source?.name ?? <span className="dim">—</span>}</td>
                  <td className="mono">{pair.target?.name ?? <span className="dim">—</span>}</td>
                  <td>
                    <span className={`g ${SIGNAL_TONE[pair.signal]}`}>{SIGNAL_GLYPH[pair.signal]}</span>
                    <span style={{ fontFamily: 'var(--sans)', marginLeft: 4 }}>
                      {pair.kind === 'Declared' ? 'paired' : pair.kind === 'ByName' ? 'name' : '—'}
                    </span>
                  </td>
                  <td style={{ fontFamily: 'var(--sans)', fontSize: 11.5 }}>{pair.detail}</td>
                  <td>
                    {/* The pair is only worth opening if both sides are readable; the schema compare is
                        where an actual answer comes from, and this screen never pretends to be one. */}
                    {pair.canBeCompared && (
                      <button
                        type="button"
                        className="chip"
                        onClick={() => {
                          onUseAsSource(pair.source!.name)
                          onUseAsTarget(pair.target!.name)
                        }}
                      >
                        Use this pair
                      </button>
                    )}
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
