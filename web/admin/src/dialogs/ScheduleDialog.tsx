import { useCallback, useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Schedule, ScheduleDay } from '../api/types'
import { Modal } from '../components/Modal'
import { errorMessage, useNotify } from '../components/notices'
import { addDays, isoDate } from '../format'
import { t } from '../i18n'

export type SchedulePreset = 'school' | 'weekend' | 'reset'

interface Base {
  minutes: string
  start: string
  end: string
}

function baseValues(base: Base) {
  return { minutes: parseInt(base.minutes, 10) || 120, start: base.start || '08:00', end: base.end || '22:00' }
}

/** Applies a preset to the week, as the old page did. dayOfWeek: 0 = Sunday … 6 = Saturday. */
export function applyPreset(days: ScheduleDay[], preset: SchedulePreset, base: Base): ScheduleDay[] {
  const b = baseValues(base)
  return days.map((d) => {
    if (preset === 'school' && d.dayOfWeek >= 1 && d.dayOfWeek <= 5)
      return { ...d, isCustom: true, dailyMinutes: 90, scheduleStart: '15:00', scheduleEnd: '20:00' }
    if (preset === 'weekend' && (d.dayOfWeek === 0 || d.dayOfWeek === 6))
      return { ...d, isCustom: true, dailyMinutes: 240, scheduleStart: '08:00', scheduleEnd: '22:00' }
    if (preset === 'reset') return { ...d, isCustom: false, dailyMinutes: b.minutes, scheduleStart: b.start, scheduleEnd: b.end }
    return d
  })
}

interface Props {
  userId: number
  username: string
  onClose: () => void
  /** Called after a successful save. */
  onSaved: () => void
}

/** The weekly schedule of one Restricted User: the standard values, custom days and single-day exceptions. */
export function ScheduleDialog({ userId, username, onClose, onSaved }: Props) {
  const notify = useNotify()
  const [loaded, setLoaded] = useState<Schedule | null>(null)
  const [base, setBase] = useState<Base>({ minutes: '120', start: '08:00', end: '22:00' })
  const [days, setDays] = useState<ScheduleDay[]>([])
  const [feedback, setFeedback] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [exc, setExc] = useState({ date: isoDate(addDays(new Date(), 1)), minutes: '120', start: '08:00', end: '22:00' })

  const load = useCallback(
    async (keepEdits: boolean) => {
      try {
        const data = await api.getSchedule(userId)
        setLoaded(data)
        if (!keepEdits) {
          setBase({ minutes: String(data.baseDailyMinutes), start: data.baseScheduleStart, end: data.baseScheduleEnd })
          setDays(data.days)
        }
      } catch (e) {
        notify.error(`${t.scheduleLoadFailed} ${errorMessage(e)}`)
        if (!keepEdits) onClose()
      }
    },
    [userId, notify, onClose],
  )

  useEffect(() => {
    void load(false)
  }, [load])

  function setDay(index: number, change: Partial<ScheduleDay>) {
    setDays((list) => list.map((d, i) => (i === index ? { ...d, ...change } : d)))
  }

  function toggleCustom(index: number, isCustom: boolean) {
    if (isCustom) {
      setDay(index, { isCustom })
      return
    }
    const b = baseValues(base)
    setDay(index, { isCustom, dailyMinutes: b.minutes, scheduleStart: b.start, scheduleEnd: b.end })
  }

  async function addException() {
    if (!exc.date) {
      notify.error(t.selectDate)
      return
    }
    try {
      await api.addScheduleException({
        userId,
        date: exc.date,
        dailyMinutes: parseInt(exc.minutes, 10) || 120,
        scheduleStart: exc.start || '08:00',
        scheduleEnd: exc.end || '22:00',
      })
      await load(true)
    } catch (e) {
      notify.error(`${t.exceptionAddFailed} ${errorMessage(e)}`)
    }
  }

  async function deleteException(id: number, date: string) {
    if (!window.confirm(t.confirmDeleteException(date))) return
    try {
      await api.deleteScheduleException(id)
      await load(true)
    } catch (e) {
      notify.error(`${t.exceptionDeleteFailed} ${errorMessage(e)}`)
    }
  }

  async function save() {
    const minutes = parseInt(base.minutes, 10)
    if (!minutes || minutes < 1 || minutes > 1440) {
      setFeedback(t.invalidBaseMinutes)
      return
    }
    if (!base.start || !base.end || base.start >= base.end) {
      setFeedback(t.invalidBaseWindow)
      return
    }
    setFeedback(null)
    setSaving(true)
    try {
      await api.saveSchedule({ userId, baseDailyMinutes: minutes, baseScheduleStart: base.start, baseScheduleEnd: base.end, days })
      onSaved()
      onClose()
    } catch (e) {
      setFeedback(`${t.scheduleSaveFailed} ${errorMessage(e)}`)
    } finally {
      setSaving(false)
    }
  }

  const exceptions = loaded?.upcomingExceptions ?? []

  return (
    <Modal
      title={`📅 ${t.scheduleTitle(username)}`}
      onClose={onClose}
      size="wide"
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            {t.cancel}
          </button>
          <button type="button" className="btn btn-primary" disabled={!loaded || saving} onClick={() => void save()}>
            💾 {saving ? t.saving : t.saveSchedule}
          </button>
        </>
      }
    >
      {!loaded ? (
        <p className="muted">{t.loading}</p>
      ) : (
        <>
          <div className="panel">
            <div className="panel-title">⚙️ {t.baseTitle}</div>
            <div className="auto-cols">
              <div>
                <label className="field" htmlFor="base-minutes">
                  {t.baseMinutes}
                </label>
                <input id="base-minutes" className="input full" type="number" min={1} max={1440} value={base.minutes} onChange={(e) => setBase({ ...base, minutes: e.target.value })} />
              </div>
              <div>
                <label className="field" htmlFor="base-start">
                  {t.baseStart}
                </label>
                <input id="base-start" className="input full" type="time" value={base.start} onChange={(e) => setBase({ ...base, start: e.target.value })} />
              </div>
              <div>
                <label className="field" htmlFor="base-end">
                  {t.baseEnd}
                </label>
                <input id="base-end" className="input full" type="time" value={base.end} onChange={(e) => setBase({ ...base, end: e.target.value })} />
              </div>
            </div>
          </div>

          <div className="row section">
            <button type="button" className="btn btn-sm btn-secondary" onClick={() => setDays(applyPreset(days, 'school', base))}>
              🎒 {t.presetSchool}
            </button>
            <button type="button" className="btn btn-sm btn-secondary" onClick={() => setDays(applyPreset(days, 'weekend', base))}>
              🎉 {t.presetWeekend}
            </button>
            <button type="button" className="btn btn-sm btn-secondary" onClick={() => setDays(applyPreset(days, 'reset', base))}>
              🔄 {t.presetReset}
            </button>
          </div>

          <div className="table-responsive">
            <table>
              <thead>
                <tr>
                  <th>{t.colDay}</th>
                  <th>{t.colCustomDay}</th>
                  <th>{t.colMinutes}</th>
                  <th>{t.colWindow}</th>
                </tr>
              </thead>
              <tbody>
                {days.map((d, i) => {
                  const name = t.dayName(d.dayOfWeek)
                  return (
                    <tr key={d.dayOfWeek}>
                      <td>
                        <strong>{name}</strong>
                      </td>
                      <td>
                        <input type="checkbox" aria-label={t.customDayLabel(name)} checked={d.isCustom} onChange={(e) => toggleCustom(i, e.target.checked)} />
                      </td>
                      <td>
                        <input
                          className="input input-sm"
                          type="number"
                          min={1}
                          max={1440}
                          aria-label={t.dayMinutesLabel(name)}
                          disabled={!d.isCustom}
                          value={d.dailyMinutes}
                          onChange={(e) => setDay(i, { dailyMinutes: parseInt(e.target.value, 10) || 120 })}
                        />
                      </td>
                      <td>
                        <div className={`row${d.isCustom ? '' : ' dimmed'}`}>
                          <input className="input input-sm" type="time" aria-label={t.dayStartLabel(name)} disabled={!d.isCustom} value={d.scheduleStart} onChange={(e) => setDay(i, { scheduleStart: e.target.value })} />
                          <span className="muted">–</span>
                          <input className="input input-sm" type="time" aria-label={t.dayEndLabel(name)} disabled={!d.isCustom} value={d.scheduleEnd} onChange={(e) => setDay(i, { scheduleEnd: e.target.value })} />
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>

          <h4 className="panel-title section">📅 {t.exceptionsTitle}</h4>
          <div className="table-responsive">
            <table>
              <thead>
                <tr>
                  <th>{t.colDate}</th>
                  <th>{t.colMinutes}</th>
                  <th>{t.colWindow}</th>
                  <th>{t.colAction}</th>
                </tr>
              </thead>
              <tbody>
                {exceptions.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="empty">
                      {t.exceptionsEmpty}
                    </td>
                  </tr>
                ) : (
                  exceptions.map((e) => (
                    <tr key={e.id}>
                      <td>
                        <strong>{e.date}</strong>
                      </td>
                      <td>{t.minutesShort(e.dailyMinutes)}</td>
                      <td>
                        {e.scheduleStart} – {e.scheduleEnd}
                      </td>
                      <td>
                        <button type="button" className="btn btn-sm btn-secondary" onClick={() => void deleteException(e.id, e.date)}>
                          🗑️ {t.delete}
                        </button>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
          <div className="row panel">
            <input className="input input-sm" type="date" aria-label={t.exceptionDate} value={exc.date} onChange={(e) => setExc({ ...exc, date: e.target.value })} />
            <input className="input input-sm" type="number" min={1} max={1440} aria-label={t.exceptionMinutes} value={exc.minutes} onChange={(e) => setExc({ ...exc, minutes: e.target.value })} />
            <input className="input input-sm" type="time" aria-label={t.exceptionStart} value={exc.start} onChange={(e) => setExc({ ...exc, start: e.target.value })} />
            <input className="input input-sm" type="time" aria-label={t.exceptionEnd} value={exc.end} onChange={(e) => setExc({ ...exc, end: e.target.value })} />
            <button type="button" className="btn btn-sm btn-secondary" onClick={() => void addException()}>
              ➕ {t.addException}
            </button>
          </div>

          {feedback && (
            <div className="banner banner-error" role="alert">
              {feedback}
            </div>
          )}
        </>
      )}
    </Modal>
  )
}
