import { useCallback, useEffect, useState } from 'react'
import { ApiError, api } from '../api/client'
import { BREAK_BREAK_MINUTES, BREAK_USE_MINUTES, type BreakStatus } from '../api/types'
import { errorMessage, useNotify } from '../components/notices'
import { clockTime } from '../format'
import { t } from '../i18n'

const RANGES = [BREAK_USE_MINUTES.min, BREAK_USE_MINUTES.max, BREAK_BREAK_MINUTES.min, BREAK_BREAK_MINUTES.max] as const

function inRange(value: number, range: { min: number; max: number }): boolean {
  return Number.isInteger(value) && value >= range.min && value <= range.max
}

/** True if N and M are whole numbers in the ranges of the Break Rule. */
export function isValidBreakRule(useMinutes: number, breakMinutes: number): boolean {
  return inRange(useMinutes, BREAK_USE_MINUTES) && inRange(breakMinutes, BREAK_BREAK_MINUTES)
}

interface RowProps {
  item: BreakStatus
  reload: () => Promise<void>
}

function BreakRow({ item, reload }: RowProps) {
  const notify = useNotify()
  const [enabled, setEnabled] = useState(item.enabled)
  const [useText, setUseText] = useState(String(item.useMinutes))
  const [breakText, setBreakText] = useState(String(item.breakMinutes))
  const [busy, setBusy] = useState(false)
  const user = item.username

  async function save() {
    const useMinutes = Number(useText.trim())
    const breakMinutes = Number(breakText.trim())
    if (useText.trim() === '' || breakText.trim() === '' || !isValidBreakRule(useMinutes, breakMinutes)) {
      notify.error(t.breakRangeError(...RANGES))
      return
    }
    setBusy(true)
    try {
      await api.setBreakRule(item.userId, { enabled, useMinutes, breakMinutes })
      notify.ok(t.breakRuleSaved(user))
      await reload()
    } catch (e) {
      notify.error(t.notSaved(errorMessage(e)))
    } finally {
      setBusy(false)
    }
  }

  async function endBreak() {
    if (!window.confirm(t.confirmEndBreak(user))) return
    setBusy(true)
    try {
      await api.endBreak(item.userId)
      notify.ok(t.breakEnded(user))
    } catch (e) {
      notify.error(e instanceof ApiError && e.status === 409 ? t.breakNotRunning : errorMessage(e))
    } finally {
      setBusy(false)
    }
    await reload()
  }

  const endsAt = item.breakEndsAt
  const running = endsAt !== null
  return (
    <tr>
      <td>
        <strong>{user}</strong>
      </td>
      <td>
        <input type="checkbox" aria-label={t.breakOnLabel(user)} checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />
      </td>
      <td>
        <input
          className="input input-sm"
          type="number"
          min={BREAK_USE_MINUTES.min}
          max={BREAK_USE_MINUTES.max}
          aria-label={t.useMinutesLabel(user)}
          value={useText}
          onChange={(e) => setUseText(e.target.value)}
        />
      </td>
      <td>
        <input
          className="input input-sm"
          type="number"
          min={BREAK_BREAK_MINUTES.min}
          max={BREAK_BREAK_MINUTES.max}
          aria-label={t.breakMinutesLabel(user)}
          value={breakText}
          onChange={(e) => setBreakText(e.target.value)}
        />
      </td>
      <td>
        {endsAt !== null ? (
          <span className="badge badge-pending">⏸️ {t.breakRunning(clockTime(endsAt))}</span>
        ) : item.enabled ? (
          <span>{t.breakUse(item.continuousUseMinutes, item.useMinutes)}</span>
        ) : (
          <span className="badge badge-neutral">{t.breakOff}</span>
        )}
      </td>
      <td className="actions-cell">
        <button type="button" className="btn btn-sm btn-primary" aria-label={t.breakSave(user)} disabled={busy} onClick={() => void save()}>
          💾 {t.save}
        </button>
        {running && (
          <button type="button" className="btn btn-sm btn-warning" aria-label={t.breakEndLabel(user)} disabled={busy} onClick={() => void endBreak()}>
            ▶️ {t.breakEnd}
          </button>
        )}
      </td>
    </tr>
  )
}

/**
 * The Break Rule of each Restricted User (on/off, N, M), the Continuous Use or the running Break, and a button that
 * ends a running Break. `tick` changes with each dashboard refresh; the card then loads the state again.
 */
export function BreaksCard({ tick }: { tick: number }) {
  const [items, setItems] = useState<BreakStatus[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setItems(await api.getBreaks())
      setLoadError(null)
    } catch (e) {
      setLoadError(errorMessage(e))
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load, tick])

  return (
    <section className="card section" aria-labelledby="breaks-title">
      <h2 id="breaks-title">⏸️ {t.breaksTitle}</h2>
      <p className="hint">{t.breaksHint(...RANGES)}</p>
      {loadError && <div className="banner banner-error">{`${t.breaksLoadFailed} ${loadError}`}</div>}
      {items === null ? (
        !loadError && <div className="muted">{t.loading}</div>
      ) : items.length === 0 ? (
        <div className="empty">{t.breaksEmpty}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colUser}</th>
                <th>{t.colBreakOn}</th>
                <th>{t.colUseMinutes}</th>
                <th>{t.colBreakMinutes}</th>
                <th>{t.colBreakState}</th>
                <th>{t.colAction}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                // A new key after a change of the rule on the server resets the fields of the row.
                <BreakRow key={`${item.userId}:${item.enabled}:${item.useMinutes}:${item.breakMinutes}`} item={item} reload={load} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
