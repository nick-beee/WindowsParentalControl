import type { Session } from '../api/types'
import { t } from '../i18n'

export interface SessionActions {
  prompt: (username: string) => void
  capture: (username: string) => void
  schedule: (userId: number, username: string) => void
  lock: (sessionId: number, username: string) => void
  grant: (userId: number, username: string, minutes: number) => void
  logoff: (sessionId: number) => void
}

function Activity({ session }: { session: Session }) {
  const text = session.isAppLive ? session.currentApp : t.idle
  return (
    <div className={`activity${session.isAppLive ? ' activity-live' : ''}`}>
      <span className={`dot ${session.isAppLive ? 'dot-live' : 'dot-idle'}`} title={session.isAppLive ? t.liveTitle : t.idleTitle} />
      <span title={text}>{text}</span>
    </div>
  )
}

export function SessionsCard({ sessions, actions }: { sessions: Session[] | null; actions: SessionActions }) {
  return (
    <section className="card" aria-labelledby="sessions-title">
      <h2 id="sessions-title">🖥️ {t.sessionsTitle}</h2>
      {sessions === null ? (
        <div className="muted">{t.loading}</div>
      ) : sessions.length === 0 ? (
        <div className="empty">{t.sessionsEmpty}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colUser}</th>
                <th>{t.colActivity}</th>
                <th>{t.colRemaining}</th>
                <th>{t.colCurfew}</th>
                <th>{t.colActions}</th>
              </tr>
            </thead>
            <tbody>
              {sessions.map((s) => (
                <tr key={s.sessionId}>
                  <td>
                    <strong>{s.username}</strong>{' '}
                    {!s.isRestricted && <span className="badge badge-admin">{t.adminBadge}</span>}
                    {s.isRestricted && s.isLocked && <span className="badge badge-pending">{t.lockedBadge}</span>}
                    <br />
                    <small>{t.sessionLabel(s.sessionId)}</small>
                  </td>
                  <td>
                    <Activity session={s} />
                  </td>
                  <td>
                    {s.isRestricted ? <strong className="accent">{t.remaining(s.remainingMinutes, s.totalAllowed)}</strong> : <span className="muted">{t.dash}</span>}
                  </td>
                  <td>{s.isRestricted ? (s.curfew === 'None' ? t.noCurfew : s.curfew) : <span className="muted">{t.dash}</span>}</td>
                  <td className="actions-cell">
                    <div className="row">
                      <button type="button" className="btn btn-sm btn-primary" onClick={() => actions.prompt(s.username)}>
                        💬 {t.actionPrompt}
                      </button>
                      {s.isRestricted && (
                        <>
                          <button type="button" className="btn btn-sm btn-primary" onClick={() => actions.capture(s.username)}>
                            📸 {t.actionScreen}
                          </button>
                          <button type="button" className="btn btn-sm btn-secondary" onClick={() => actions.schedule(s.userId, s.username)}>
                            📅 {t.actionSchedule}
                          </button>
                        </>
                      )}
                      <button type="button" className="btn btn-sm btn-warning" onClick={() => actions.lock(s.sessionId, s.username)}>
                        🔒 {t.actionLock}
                      </button>
                      {s.isRestricted && (
                        <>
                          <button type="button" className="btn btn-sm btn-success" onClick={() => actions.grant(s.userId, s.username, 15)}>
                            {t.actionGrant(15)}
                          </button>
                          <button type="button" className="btn btn-sm btn-success" onClick={() => actions.grant(s.userId, s.username, 30)}>
                            {t.actionGrant(30)}
                          </button>
                        </>
                      )}
                      <button type="button" className="btn btn-sm btn-danger" onClick={() => actions.logoff(s.sessionId)}>
                        {t.actionLogoff}
                      </button>
                    </div>
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
