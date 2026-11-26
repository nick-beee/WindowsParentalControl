import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { UpdateCheck } from '../api/types'
import { Modal } from '../components/Modal'
import { errorMessage, useNotify } from '../components/notices'
import { t } from '../i18n'

/** The lines of the release notes to show: no headings, no empty lines, no changelog link. */
export function releaseNoteLines(notes: string | null): string[] {
  return (notes ?? '')
    .split('\n')
    .map((l) => l.trim())
    .filter((l) => l.length > 0 && !l.startsWith('##') && !l.startsWith('**Full Changelog**'))
    .map((l) => (l.startsWith('* ') || l.startsWith('- ') ? `• ${l.substring(2)}` : l))
}

interface Props {
  onClose: () => void
  /** Reports the result of the check to the header (update badge). */
  onChecked: (result: UpdateCheck) => void
}

export function UpdateDialog({ onClose, onChecked }: Props) {
  const notify = useNotify()
  const [data, setData] = useState<UpdateCheck | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [applying, setApplying] = useState(false)

  useEffect(() => {
    let active = true
    api
      .checkUpdate()
      .then((result) => {
        if (!active) return
        setData(result)
        onChecked(result)
      })
      .catch((e: unknown) => active && setError(errorMessage(e)))
    return () => {
      active = false
    }
  }, [onChecked])

  async function apply() {
    setApplying(true)
    try {
      const res = await api.applyUpdate()
      if (res.success) notify.ok(res.message || t.updateStarted)
      else notify.error(t.updateApplyFailed(res.message || ''))
      onClose()
    } catch (e) {
      notify.error(t.updateApplyFailed(errorMessage(e)))
    } finally {
      setApplying(false)
    }
  }

  const failure = error ?? data?.error ?? null
  const lines = releaseNoteLines(data?.releaseNotes ?? null)

  return (
    <Modal
      title={`🚀 ${t.updateTitle}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            {t.close}
          </button>
          {data?.hasUpdate && !failure && (
            <button type="button" className="btn btn-success" disabled={applying} onClick={() => void apply()}>
              ⚡ {applying ? t.updateApplying : t.updateApply}
            </button>
          )}
        </>
      }
    >
      {failure ? (
        <div className="banner banner-error">{t.updateFailed(failure)}</div>
      ) : data === null ? (
        <p className="modal-text">{t.updateChecking}</p>
      ) : data.hasUpdate ? (
        <>
          <span className="badge badge-approved">{t.updateNew}</span>
          <p className="accent">
            <strong>{data.releaseTitle || `v${data.latestVersion ?? ''}`}</strong>
          </p>
          <p className="hint">{t.updateVersions(data.currentVersion, `v${data.latestVersion ?? ''}`)}</p>
          <div className="notes">
            {lines.length === 0 ? <p>{t.updateNotesFallback}</p> : lines.map((line, i) => <p key={i}>{line}</p>)}
          </div>
          <div className="banner banner-warn">⚠️ {t.updateWarning}</div>
        </>
      ) : (
        <div className="banner banner-ok">✅ {t.updateCurrent(data.currentVersion)}</div>
      )}
    </Modal>
  )
}
