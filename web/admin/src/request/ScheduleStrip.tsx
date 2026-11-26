import { useState } from 'react'
import type { RequestDay, Translate } from './data'

/** The week of the Restricted User: one button per day and the card of the selected day (today at the start). */
export function ScheduleStrip({ days, t }: { days: RequestDay[]; t: Translate }) {
  const [selected, setSelected] = useState(() => Math.max(0, days.findIndex((d) => d.isToday)))
  const day = days[selected]
  if (!day) return null

  const badge = day.hasException
    ? { text: t('badge_exception'), className: 'badge-tag exception' }
    : day.isCustom
      ? { text: t('badge_custom'), className: 'badge-tag custom' }
      : { text: t('badge_standard'), className: 'badge-tag' }

  return (
    <div className="schedule-panel">
      <div className="schedule-header">
        <span className="schedule-title">{t('schedule_title')}</span>
        <span className="schedule-baseline">{t('schedule_default')}</span>
      </div>

      <div className="day-strip">
        {days.map((d, i) => (
          <button
            key={d.dayCode}
            type="button"
            className={`day-pill${i === selected ? ' active' : ''}${d.isToday ? ' today' : ''}`}
            aria-pressed={i === selected}
            aria-label={d.dayName}
            onClick={() => setSelected(i)}
          >
            <span className="pill-code">{d.dayCode}</span>
            {d.hasException ? (
              <span className="pill-dot dot-exception" title={t('dot_exception')}>
                ★
              </span>
            ) : d.isCustom ? (
              <span className="pill-dot dot-custom" title={t('dot_custom')}>
                •
              </span>
            ) : null}
          </button>
        ))}
      </div>

      <div className="active-day-card">
        <div className="card-day-header">
          <div>
            <span className="card-day-title">{day.dayName + (day.isToday ? ` ${t('today_suffix')}` : '')}</span>
            <span className="card-day-subtitle">
              {day.hasException ? `• ${day.exceptionNote || t('badge_exception')}` : ''}
            </span>
          </div>
          <span className={badge.className}>{badge.text}</span>
        </div>
        <div className="card-day-metrics">
          <div className="metric-box">
            <span className="metric-label">{t('metric_quota')}</span>
            <span className="metric-val">{day.minutesText}</span>
          </div>
          <div className="metric-box">
            <span className="metric-label">{t('metric_window')}</span>
            <span className="metric-val">{day.timeWindow}</span>
          </div>
        </div>
      </div>
    </div>
  )
}
