import { Fragment, useState, type FormEvent } from 'react'
import { sendRequest, type ExtensionRequest, type ScheduleChangeRequest } from './api'
import { Countdown } from './Countdown'
import { translator, type RequestPageData, type Translate } from './data'
import { fillNodes } from './fill'
import { requestLimit } from './rules'
import { ScheduleStrip } from './ScheduleStrip'

type Tab = 'ext' | 'sched'
type Feedback = { kind: 'success' | 'error'; text: string }

/** The child request page (/request): remaining time, week, status of the last request and the two request forms. */
export function RequestApp({ data }: { data: RequestPageData }) {
  const t = translator(data.texts)
  const limit = requestLimit(data, t)
  // null after a request was sent: both forms are hidden; a tab shows its form again, as on the old page.
  const [visible, setVisible] = useState<Tab | null>('ext')
  const [tab, setTab] = useState<Tab>('ext')
  const [feedback, setFeedback] = useState<Feedback | null>(null)

  const selectTab = (next: Tab) => {
    setTab(next)
    setVisible(next)
  }

  /** Sends a request. Returns true if the service did not accept it, so the form can be used again. */
  const submit = async (body: ExtensionRequest | ScheduleChangeRequest, failedKey: string): Promise<boolean> => {
    const result = await sendRequest(body)
    if (result.kind === 'sent') {
      setFeedback({ kind: 'success', text: result.message })
      setVisible(null)
      return false
    }
    const text = result.kind === 'refused' ? (result.error ?? t(failedKey)) : t('connect_failed')
    setFeedback({ kind: 'error', text })
    return true
  }

  return (
    <div className="card">
      <div className="header-row">
        <h1>{t('heading')}</h1>
        <div className="header-badges">
          <SessionBadge state={data.sessionState} t={t} />
          <div className="user-badge">
            {t('account')} <strong>{data.username}</strong>
          </div>
        </div>
      </div>

      <div className="timer-panel">
        <div className="timer-label">{t('timer_label')}</div>
        <Countdown seconds={data.remainingSeconds} ticking={data.ticking} overText={t('timer_over')} />
        <div className="timer-curfew">
          {t('timer_curfew')} <strong>{data.curfew}</strong>
        </div>
      </div>

      {data.declineReason ? (
        <div className="banner-decline">
          <div className="decline-header">
            <span className="decline-icon">🚫</span>
            <span className="decline-title">{t('decline_title')}</span>
          </div>
          <div className="decline-text">&quot;{data.declineReason}&quot;</div>
        </div>
      ) : null}

      {data.days.length > 0 ? <ScheduleStrip days={data.days} t={t} /> : null}

      {data.adminTest ? (
        <div className="banner banner-info banner-admin">
          🔧 <strong>{data.adminTest.title}</strong>: {fillNodes(data.adminTest.text, { user: <strong>{data.username}</strong> })}
          {data.adminTest.users.length > 0 ? (
            <div className="admin-switch">
              {data.adminTest.switchLabel}{' '}
              {data.adminTest.users.map((user, i) => (
                <Fragment key={user}>
                  {i > 0 ? ' | ' : null}
                  <a href={`/request?user=${encodeURIComponent(user)}`}>{user}</a>
                </Fragment>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}

      {data.isOffline ? <div className="banner banner-error">{t('offline')}</div> : null}

      {limit.key ? (
        <div className={`banner banner-${limit.kind}`}>
          {fillNodes(t(limit.key), { status: <strong>{limit.status}</strong> })}
        </div>
      ) : null}

      {feedback ? (
        <div className={`banner banner-${feedback.kind} feedback`} role={feedback.kind === 'error' ? 'alert' : 'status'}>
          {feedback.kind === 'success' ? '✅ ' : '❌ '}
          {feedback.text}
        </div>
      ) : null}

      <div className="tabs" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'ext'}
          className={`tab-btn${tab === 'ext' ? ' active' : ''}`}
          onClick={() => selectTab('ext')}
        >
          {t('tab_extension')}
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'sched'}
          className={`tab-btn${tab === 'sched' ? ' active' : ''}`}
          onClick={() => selectTab('sched')}
        >
          {t('tab_schedule')}
        </button>
      </div>

      <ExtensionForm data={data} t={t} hidden={visible !== 'ext'} disabled={limit.disabled} submit={submit} />
      <ScheduleForm data={data} t={t} hidden={visible !== 'sched'} disabled={limit.disabled} submit={submit} />
    </div>
  )
}

function SessionBadge({ state, t }: { state: RequestPageData['sessionState']; t: Translate }) {
  switch (state) {
    case 'inactive':
      return <span className="session-badge badge-offline">{t('session_inactive')}</span>
    case 'locked':
      return <span className="session-badge badge-locked">{t('session_locked')}</span>
    default:
      return <span className="session-badge badge-active">{t('session_active')}</span>
  }
}

interface FormProps {
  data: RequestPageData
  t: Translate
  /** The form stays in the page when it is hidden, so a typed reason stays after a tab switch. */
  hidden: boolean
  disabled: boolean
  submit: (body: ExtensionRequest | ScheduleChangeRequest, failedKey: string) => Promise<boolean>
}

function ExtensionForm({ data, t, hidden, disabled, submit }: FormProps) {
  const [minutes, setMinutes] = useState(data.extensionOptions[0]?.minutes ?? 15)
  const [reason, setReason] = useState('')
  const [sending, setSending] = useState(false)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setSending(true)
    const retry = await submit({ requestType: 'extension', minutes, reason, username: data.username }, 'ext_failed')
    if (retry) setSending(false)
  }

  return (
    <form id="formExt" hidden={hidden} onSubmit={onSubmit}>
      <label className="field-label">{t('ext_choose')}</label>
      <div className="radio-group">
        {data.extensionOptions.map((o) => (
          <div className="radio-btn" key={o.minutes}>
            <input
              type="radio"
              id={`m${o.minutes}`}
              name="minutes"
              value={o.minutes}
              checked={minutes === o.minutes}
              disabled={disabled}
              onChange={() => setMinutes(o.minutes)}
            />
            <label htmlFor={`m${o.minutes}`}>{o.label}</label>
          </div>
        ))}
      </div>

      <div className="form-group">
        <label htmlFor="extReason" className="field-label">
          {t('ext_reason_label')}
        </label>
        <textarea
          id="extReason"
          placeholder={t('ext_reason_placeholder')}
          required
          disabled={disabled}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
        />
      </div>

      <button type="submit" className="submit-btn" disabled={disabled || sending}>
        {sending ? t('ext_sending') : t('ext_submit')}
      </button>
    </form>
  )
}

function ScheduleForm({ data, t, hidden, disabled, submit }: FormProps) {
  const [targetDate, setTargetDate] = useState(data.dates.tomorrow)
  const [minutes, setMinutes] = useState(120)
  const [start, setStart] = useState('08:00')
  const [end, setEnd] = useState('21:00')
  const [reason, setReason] = useState('')
  const [sending, setSending] = useState(false)

  const onSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setSending(true)
    const retry = await submit(
      {
        requestType: 'schedule_change',
        targetDate,
        minutes,
        requestedStart: start,
        requestedEnd: end,
        reason,
        username: data.username,
      },
      'sched_failed',
    )
    if (retry) setSending(false)
  }

  return (
    <form id="formSched" hidden={hidden} onSubmit={onSubmit}>
      <div className="form-row">
        <div>
          <label htmlFor="schedDate" className="field-label">
            {t('sched_date')}
          </label>
          <input
            type="date"
            id="schedDate"
            value={targetDate}
            min={data.dates.min}
            max={data.dates.max}
            required
            disabled={disabled}
            onChange={(e) => setTargetDate(e.target.value)}
          />
        </div>
        <div>
          <label htmlFor="schedMinutes" className="field-label">
            {t('sched_minutes')}
          </label>
          <select id="schedMinutes" value={minutes} disabled={disabled} onChange={(e) => setMinutes(Number(e.target.value))}>
            {data.scheduleOptions.map((o) => (
              <option key={o.minutes} value={o.minutes}>
                {o.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="form-row">
        <div>
          <label htmlFor="schedStart" className="field-label">
            {t('sched_start')}
          </label>
          <input type="time" id="schedStart" value={start} required disabled={disabled} onChange={(e) => setStart(e.target.value)} />
        </div>
        <div>
          <label htmlFor="schedEnd" className="field-label">
            {t('sched_end')}
          </label>
          <input type="time" id="schedEnd" value={end} required disabled={disabled} onChange={(e) => setEnd(e.target.value)} />
        </div>
      </div>

      <div className="form-group">
        <label htmlFor="schedReason" className="field-label">
          {t('sched_reason_label')}
        </label>
        <textarea
          id="schedReason"
          placeholder={t('sched_reason_placeholder')}
          required
          disabled={disabled}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
        />
      </div>

      <button type="submit" className="submit-btn" disabled={disabled || sending}>
        {sending ? t('sched_sending') : t('sched_submit')}
      </button>
    </form>
  )
}
