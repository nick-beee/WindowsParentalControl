import { useState } from 'react'
import { api } from '../api/client'
import type { AppRule, AppUsage, Category, Dashboard, DashboardQuery } from '../api/types'
import { CategorySelect } from '../components/CategorySelect'
import { errorMessage, useNotify } from '../components/notices'
import { addDays, exeFileName, formatMinutes, isoDate } from '../format'
import { t } from '../i18n'

const PALETTE = ['#38bdf8', '#818cf8', '#34d399', '#f472b6', '#fbbf24', '#a78bfa', '#f87171', '#fb923c', '#2dd4bf', '#a3e635']
const HOURS = Array.from({ length: 24 }, (_, h) => h)

export type DatePreset = 'today' | 'yesterday' | 'week' | 'custom'

/** The query of a date preset, relative to `now`. */
export function presetQuery(preset: Exclude<DatePreset, 'custom'>, now = new Date()): Pick<DashboardQuery, 'date' | 'range'> {
  switch (preset) {
    case 'yesterday':
      return { date: isoDate(addDays(now, -1)), range: 'day' }
    case 'week':
      return { date: isoDate(now), range: 'week' }
    default:
      return { date: isoDate(now), range: 'day' }
  }
}

function barColor(minutes: number): string {
  if (minutes <= 0) return '#1e293b'
  if (minutes <= 15) return '#0284c7'
  if (minutes <= 30) return '#38bdf8'
  if (minutes <= 45) return '#818cf8'
  return '#f59e0b'
}

function Timeline({ hours, date, range }: { hours: number[]; date: string; range: string }) {
  const max = Math.max(1, ...hours)
  return (
    <div className="timeline-card">
      <div className="timeline-header">
        <strong>{t.timelineTitle}</strong>
        <span>{range === 'week' ? t.timelineWeek(date) : t.timelineDay(date)}</span>
      </div>
      {hours.length === 24 && (
        <div className="timeline-grid">
          {HOURS.map((h) => {
            const m = hours[h] ?? 0
            const height = m === 0 ? 8 : Math.max(12, Math.round((m / max) * 100))
            return <div key={h} className="timeline-bar" style={{ height: `${height}%`, background: barColor(m) }} title={t.timelineHour(h, m)} />
          })}
        </div>
      )}
      <div className="timeline-labels" aria-hidden="true">
        {HOURS.map((h) => (
          <span key={h}>{String(h).padStart(2, '0')}</span>
        ))}
      </div>
    </div>
  )
}

interface Props {
  data: Dashboard | null
  query: DashboardQuery
  preset: DatePreset
  onUser: (user: string) => void
  onPreset: (preset: Exclude<DatePreset, 'custom'>) => void
  onDate: (date: string) => void
  rules: AppRule[] | null
  reloadCategories: () => Promise<void>
  reloadDashboard: () => void
}

/** App Usage with the 24-hour timeline and the category assignment of an app. */
export function ActivityPanel({ data, query, preset, onUser, onPreset, onDate, rules, reloadCategories, reloadDashboard }: Props) {
  const notify = useNotify()
  const apps = data?.appUsage ?? []
  const users = data?.availableUsers ?? []
  const selectedUser = data?.selectedUser ?? query.user
  const [pending, setPending] = useState<string | null>(null)

  async function assign(app: AppUsage, category: Category | null) {
    if (!category) return
    const fileName = exeFileName(app.processName)
    const existing = rules?.find((r) => r.matchType === 'file' && r.pattern.toLowerCase() === fileName.toLowerCase())
    setPending(app.processName)
    try {
      if (existing) {
        await api.updateAppRule(existing.id, {
          matchType: 'file',
          pattern: existing.pattern,
          category,
          weekdayMinutes: existing.weekdayMinutes,
          weekendMinutes: existing.weekendMinutes,
        })
      } else {
        await api.addAppRule({ matchType: 'file', pattern: fileName, category })
      }
      await reloadCategories()
      reloadDashboard()
    } catch (e) {
      notify.error(t.notAssigned(errorMessage(e)))
    } finally {
      setPending(null)
    }
  }

  return (
    <section className="card section" aria-labelledby="activity-title">
      <div className="row-between">
        <div>
          <h2 id="activity-title">📊 {t.activityTitle}</h2>
          <p className="hint">{t.activityHint}</p>
        </div>
        <div className="total">{t.totalTime(formatMinutes(data?.totalMinutes ?? 0))}</div>
      </div>

      <div className="filters">
        <div className="row" role="group" aria-label={t.colUser}>
          <button type="button" className="pill" aria-pressed={selectedUser.toLowerCase() === 'all'} onClick={() => onUser('all')}>
            {t.allUsers}
          </button>
          {users.map((u) => (
            <button key={u} type="button" className="pill" aria-pressed={selectedUser.toLowerCase() === u.toLowerCase()} onClick={() => onUser(u)}>
              {u}
            </button>
          ))}
        </div>
        <div className="row">
          <button type="button" className="pill" aria-pressed={preset === 'today'} onClick={() => onPreset('today')}>
            📅 {t.today}
          </button>
          <button type="button" className="pill" aria-pressed={preset === 'yesterday'} onClick={() => onPreset('yesterday')}>
            {t.yesterday}
          </button>
          <button type="button" className="pill" aria-pressed={preset === 'week'} onClick={() => onPreset('week')}>
            {t.pastWeek}
          </button>
          <input type="date" className="input input-sm" aria-label={t.dateInputLabel} value={query.date} onChange={(e) => e.target.value && onDate(e.target.value)} />
        </div>
      </div>

      <Timeline hours={data?.hourlyTimeline ?? []} date={data?.selectedDate ?? query.date} range={data?.selectedRange ?? query.range} />

      <div className="legend">
        <span>{t.topApps}</span>
        <span>{t.appsLogged(apps.length)}</span>
      </div>
      <div className="stacked">
        {apps.length === 0 ? (
          <div className="stacked-empty" />
        ) : (
          apps.slice(0, PALETTE.length).map((a, i) => (
            <div key={`${a.userId}-${a.processName}`} style={{ width: `${a.percentage}%`, background: PALETTE[i] }} title={`${a.processName}: ${a.minutes} Min. (${a.percentage}%)`} />
          ))
        )}
      </div>

      {data === null ? (
        <div className="muted">{t.loading}</div>
      ) : apps.length === 0 ? (
        <div className="empty">{t.activityEmpty}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colUser}</th>
                <th>{t.colProcess}</th>
                <th>{t.colWindowTitle}</th>
                <th>{t.colShare}</th>
                <th>{t.colDuration}</th>
                <th>{t.colCategory}</th>
              </tr>
            </thead>
            <tbody>
              {apps.map((a, i) => {
                const color = PALETTE[i % PALETTE.length]
                return (
                  <tr key={`${a.userId}-${a.processName}`}>
                    <td>
                      <strong>{a.username}</strong>
                    </td>
                    <td>
                      <div className="row">
                        <span className="swatch" style={{ background: color }} />
                        <code>{a.processName}</code>
                      </div>
                    </td>
                    <td className="ellipsis" title={a.windowTitle || '-'}>
                      {a.windowTitle || '-'}
                    </td>
                    <td>
                      <small>{a.percentage}%</small>
                      <div className="track">
                        <div className="fill" style={{ width: `${a.percentage}%`, background: color }} />
                      </div>
                    </td>
                    <td>
                      <strong>{formatMinutes(a.minutes)}</strong>
                    </td>
                    <td>
                      <CategorySelect
                        value={a.fileRuleCategory}
                        withEmpty
                        label={t.assignCategory(a.processName)}
                        onChange={(c) => void (pending === null && assign(a, c))}
                      />
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
      <footer className="foot">{t.footer}</footer>
    </section>
  )
}
