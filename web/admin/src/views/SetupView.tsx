import { t } from '../i18n'

/** Before the setup of the admin sign-in (#21): only a text, no form, no data. */
export function SetupView() {
  return (
    <main className="center-page">
      <div className="auth-card">
        <h1 className="accent">🔒 {t.setupTitle}</h1>
        <p>{t.setupText}</p>
        <p className="small">{t.setupHint}</p>
      </div>
    </main>
  )
}
