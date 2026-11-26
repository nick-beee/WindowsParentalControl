import type { GraceRequest } from '../api/types'
import { t } from '../i18n'

interface Props {
  requests: GraceRequest[] | null
  onResolve: (request: GraceRequest, action: 'approve' | 'decline') => void
}

/** The Grace Requests of today. A pending request gets approve and decline buttons. */
export function RequestsCard({ requests, onResolve }: Props) {
  return (
    <section className="card" aria-labelledby="requests-title">
      <h2 id="requests-title">🎮 {t.requestsTitle}</h2>
      {requests === null ? (
        <div className="muted">{t.loading}</div>
      ) : requests.length === 0 ? (
        <div className="empty">{t.requestsEmpty}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colUser}</th>
                <th>{t.colRequest}</th>
                <th>{t.colReason}</th>
                <th>{t.colAction}</th>
              </tr>
            </thead>
            <tbody>
              {requests.map((r) => {
                const status = String(r.status)
                return (
                  <tr key={r.id}>
                    <td>
                      <strong>{r.username}</strong>
                      <br />
                      <small>{r.createdAt}</small>
                    </td>
                    <td>{t.requestMinutes(r.requestedMinutes)}</td>
                    <td className="wrap">{r.reason ?? ''}</td>
                    <td>
                      {status === 'PENDING' ? (
                        <div className="row">
                          <button type="button" className="btn btn-sm btn-success" onClick={() => onResolve(r, 'approve')}>
                            {t.approve}
                          </button>
                          <button type="button" className="btn btn-sm btn-danger" onClick={() => onResolve(r, 'decline')}>
                            {t.decline}
                          </button>
                        </div>
                      ) : (
                        <span className={`badge badge-${status === 'APPROVED' ? 'approved' : status === 'DECLINED' ? 'declined' : 'neutral'}`}>
                          {t.requestStatus(status)}
                        </span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
