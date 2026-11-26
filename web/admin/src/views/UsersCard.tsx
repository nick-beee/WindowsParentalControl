import type { ManagedUser } from '../api/types'
import { t } from '../i18n'

interface Props {
  users: ManagedUser[] | null
  onManage: (userId: number, username: string) => void
}

export function UsersCard({ users, onManage }: Props) {
  return (
    <section className="card section" aria-labelledby="users-title">
      <h2 id="users-title">👥 {t.usersTitle}</h2>
      <p className="hint">{t.usersHint}</p>
      {users === null ? (
        <div className="muted">{t.loading}</div>
      ) : users.length === 0 ? (
        <div className="empty">{t.usersEmpty}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colUser}</th>
                <th>{t.colQuota}</th>
                <th>{t.colWindow}</th>
                <th>{t.colCustom}</th>
                <th>{t.colAction}</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td>
                    <strong>{u.username}</strong>
                  </td>
                  <td>
                    <strong className="accent">{t.minutesShort(u.baseMinutes)}</strong>
                  </td>
                  <td>
                    {u.baseStart} – {u.baseEnd}
                  </td>
                  <td>
                    {u.customCount > 0 ? (
                      <span className="badge badge-custom">{t.customDays(u.customCount)}</span>
                    ) : (
                      <span className="badge badge-neutral">{t.allStandard}</span>
                    )}
                  </td>
                  <td>
                    <button type="button" className="btn btn-sm btn-primary" onClick={() => onManage(u.id, u.username)}>
                      ⚙️ {t.manageSchedule}
                    </button>
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
