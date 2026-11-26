import { useState } from 'react'
import type { PromptSend, PromptUrgency } from '../api/types'
import { Modal } from '../components/Modal'
import { t } from '../i18n'

interface Props {
  username: string
  onSend: (prompt: PromptSend) => void
  onClose: () => void
}

/** An Interactive Session Prompt to one user. */
export function PromptDialog({ username, onSend, onClose }: Props) {
  const [message, setMessage] = useState('')
  const [targetDisplay, setTargetDisplay] = useState(-1)
  const [urgency, setUrgency] = useState<PromptUrgency>('NORMAL')
  const [options, setOptions] = useState('')
  const [error, setError] = useState<string | null>(null)

  function send() {
    const text = message.trim()
    if (!text) {
      setError(t.promptEmpty)
      return
    }
    onSend({ username, message: text, urgency, targetDisplay, customOptions: options.trim() || null })
  }

  return (
    <Modal
      title={`💬 ${t.promptTitle(username)}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            {t.cancel}
          </button>
          <button type="button" className="btn btn-primary" onClick={send}>
            {t.promptSend} 📤
          </button>
        </>
      }
    >
      <p className="modal-text">{t.promptText}</p>
      {error && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}
      <div className="stack">
        <div>
          <label className="field" htmlFor="prompt-message">
            {t.promptMessageLabel}
          </label>
          <textarea id="prompt-message" className="full" rows={3} placeholder={t.promptMessagePlaceholder} value={message} onChange={(e) => setMessage(e.target.value)} />
        </div>
        <div className="two-col">
          <div>
            <label className="field" htmlFor="prompt-monitor">
              {t.promptMonitorLabel}
            </label>
            <select id="prompt-monitor" className="full" value={targetDisplay} onChange={(e) => setTargetDisplay(Number(e.target.value))}>
              <option value={-1}>{t.monitorAuto}</option>
              <option value={0}>{t.monitorPrimary}</option>
              <option value={1}>{t.monitorSecondary}</option>
            </select>
          </div>
          <div>
            <label className="field" htmlFor="prompt-urgency">
              {t.promptUrgencyLabel}
            </label>
            <select id="prompt-urgency" className="full" value={urgency} onChange={(e) => setUrgency(e.target.value as PromptUrgency)}>
              <option value="NORMAL">{t.urgencyNormal}</option>
              <option value="URGENT">🚨 {t.urgencyUrgent}</option>
            </select>
          </div>
        </div>
        <div>
          <label className="field" htmlFor="prompt-options">
            {t.promptOptionsLabel}
          </label>
          <input id="prompt-options" className="input full" placeholder={t.promptOptionsPlaceholder} value={options} onChange={(e) => setOptions(e.target.value)} />
        </div>
      </div>
    </Modal>
  )
}
