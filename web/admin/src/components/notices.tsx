import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react'
import { ApiError } from '../api/client'
import { t } from '../i18n'

type Kind = 'ok' | 'error'
interface Notice {
  id: number
  kind: Kind
  text: string
}

interface Notify {
  ok: (text: string) => void
  error: (text: string) => void
}

const NoticeContext = createContext<Notify>({ ok: () => {}, error: () => {} })

/** Short messages in the corner of the page. They replace the alert() calls of the old page. */
export function NoticeProvider({ children }: { children: ReactNode }) {
  const [notices, setNotices] = useState<Notice[]>([])
  const nextId = useRef(1)

  const dismiss = useCallback((id: number) => setNotices((list) => list.filter((n) => n.id !== id)), [])
  const push = useCallback(
    (kind: Kind, text: string) => {
      const id = nextId.current++
      setNotices((list) => [...list.slice(-3), { id, kind, text }])
      window.setTimeout(() => dismiss(id), kind === 'ok' ? 4000 : 8000)
    },
    [dismiss],
  )
  const notify = useMemo<Notify>(() => ({ ok: (text) => push('ok', text), error: (text) => push('error', text) }), [push])

  return (
    <NoticeContext.Provider value={notify}>
      {children}
      <div className="notices" aria-live="polite">
        {notices.map((n) => (
          <div key={n.id} className={`banner banner-${n.kind}`} role={n.kind === 'error' ? 'alert' : 'status'}>
            <span>{n.text}</span>
            <button type="button" className="btn btn-link" aria-label={t.close} onClick={() => dismiss(n.id)}>
              ✕
            </button>
          </div>
        ))}
      </div>
    </NoticeContext.Provider>
  )
}

export function useNotify(): Notify {
  return useContext(NoticeContext)
}

/** The text of an error for a notice: the server message of an ApiError, or a network error. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return error.message
  return t.networkError
}
