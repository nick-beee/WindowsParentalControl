import { useState } from 'react'
import { Modal } from '../components/Modal'
import { t } from '../i18n'

/** The warning times before the lock. The service accepts 5 to 60 seconds. */
export const LOCK_DELAYS = [5, 15, 30, 60] as const

interface Props {
  username: string
  onConfirm: (seconds: number) => void
  onClose: () => void
}

export function LockDialog({ username, onConfirm, onClose }: Props) {
  const [seconds, setSeconds] = useState<number>(15)
  return (
    <Modal
      title={`🔒 ${t.lockTitle}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            {t.cancel}
          </button>
          <button type="button" className="btn btn-warning" onClick={() => onConfirm(seconds)}>
            {t.lockConfirm}
          </button>
        </>
      }
    >
      <p className="modal-text">{t.lockText(username)}</p>
      <label className="field" htmlFor="lock-delay">
        {t.lockDelayLabel}
      </label>
      <select id="lock-delay" className="full" value={seconds} onChange={(e) => setSeconds(Number(e.target.value))}>
        {LOCK_DELAYS.map((s) => (
          <option key={s} value={s}>
            {t.lockDelay(s)}
          </option>
        ))}
      </select>
    </Modal>
  )
}
