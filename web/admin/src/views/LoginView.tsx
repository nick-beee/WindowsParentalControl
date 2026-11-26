import { useState, type FormEvent } from 'react'
import { ADMIN_PAGE, api } from '../api/client'
import type { LoginOutcome } from '../api/types'
import { t } from '../i18n'

/** The texts when the answer has no message of the service. */
const MESSAGES: Record<Exclude<LoginOutcome, 'ok'>, string> = {
  invalid: t.loginInvalid,
  refused: t.loginRefused,
  blocked: t.loginBlocked,
}

interface Props {
  /** The service refused this Windows account (#21): show the text, the form stays for another try. */
  refused?: boolean
  /** Called after a successful login. Default: reload /admin, so the service serves the app view. */
  onSignedIn?: () => void
}

export function LoginView({ refused = false, onSignedIn = () => window.location.assign(ADMIN_PAGE) }: Props) {
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(refused ? t.loginRefused : null)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!/^\d{6}$/.test(code)) {
      setError(t.loginFormat)
      return
    }
    setBusy(true)
    setError(null)
    try {
      const result = await api.login(code)
      if (result.outcome === 'ok') {
        onSignedIn()
        return
      }
      setError(result.message ?? MESSAGES[result.outcome])
      setCode('')
    } catch {
      setError(t.networkError)
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="center-page">
      <form className="auth-card" onSubmit={submit} noValidate>
        <h1 className="accent">🛡️ {t.loginTitle}</h1>
        <p>{t.loginText}</p>
        {error && (
          <div className="banner banner-error" role="alert">
            {error}
          </div>
        )}
        <label className="field" htmlFor="code">
          {t.loginCodeLabel}
        </label>
        <input
          id="code"
          className="code-input"
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={6}
          placeholder="000000"
          autoFocus
          value={code}
          onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
        />
        <button type="submit" className="btn btn-primary" disabled={busy}>
          {busy ? t.loginBusy : t.loginButton}
        </button>
      </form>
    </main>
  )
}
