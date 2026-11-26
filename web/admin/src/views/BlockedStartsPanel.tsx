import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { BlockedStarts } from '../api/types'
import { errorMessage } from '../components/notices'
import { formatClock } from '../format'
import { t } from '../i18n'

interface Props {
  /** A change reloads the list (the dashboard refresh). */
  tick: number
}

/**
 * The blocked starts of one day, per Restricted User and program (`GET /api/admin/blocked-starts`).
 * The file name and the path come from the child's PC: React renders them as text.
 */
export function BlockedStartsPanel({ tick }: Props) {
  // An empty date asks for the today of the service; the answer names that date.
  const [date, setDate] = useState('')
  const [data, setData] = useState<BlockedStarts | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    api
      .getBlockedStarts(date || undefined)
      .then((result) => {
        if (!active) return
        setData(result)
        setLoadError(null)
      })
      .catch((e: unknown) => active && setLoadError(errorMessage(e)))
    return () => {
      active = false
    }
  }, [date, tick])

  return (
    <section className="card section" aria-labelledby="blocked-starts-title">
      <h2 id="blocked-starts-title">🚫 {t.blockedStartsTitle}</h2>
      <p className="hint">{t.blockedStartsHint}</p>
      <div className="row">
        <input
          className="input input-sm"
          type="date"
          aria-label={t.blockedStartsDate}
          value={date || data?.date || ''}
          onChange={(e) => setDate(e.target.value)}
        />
      </div>
      {loadError && <div className="banner banner-error">{t.blockedStartsLoadFailed(loadError)}</div>}
      {data === null ? (
        !loadError && <div className="muted">{t.loading}</div>
      ) : data.starts.length === 0 ? (
        <div className="empty">{t.blockedStartsEmpty}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colUser}</th>
                <th>{t.colProcess}</th>
                <th>{t.colClosed}</th>
                <th>{t.colSuspended}</th>
                <th>{t.colFirstTry}</th>
                <th>{t.colLastTry}</th>
                <th>{t.colPath}</th>
              </tr>
            </thead>
            <tbody>
              {data.starts.map((s) => (
                <tr key={`${s.username}:${s.fileName}`}>
                  <td>{s.username}</td>
                  <td>
                    <code>{s.fileName}</code>
                  </td>
                  <td>{s.closedCount}</td>
                  <td>{s.suspendedCount}</td>
                  <td>{formatClock(s.firstAt)}</td>
                  <td>{formatClock(s.lastAt)}</td>
                  <td>
                    <small>{s.exePath}</small>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
