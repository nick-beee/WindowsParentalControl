import { useState } from 'react'
import { captureImageUrl } from '../api/client'
import type { Capture } from '../api/types'
import { Modal } from '../components/Modal'
import { t } from '../i18n'
import type { CaptureState } from './useCaptures'

function meta(c: Capture): string {
  const kb = Math.round(c.fileSizeBytes / 1024)
  return `${t.captureMeta(c.width, c.height, kb)} • ${t.captureTrigger(c.triggerType)}`
}

export function CapturePanel({ state }: { state: CaptureState }) {
  const [zoom, setZoom] = useState(false)
  const { selected, recent } = state

  return (
    <section className="card section" aria-labelledby="capture-title">
      <div className="row-between">
        <div className="row">
          <h2 id="capture-title">📸 {t.capturesTitle}</h2>
          {state.watching && <span className="badge badge-live">● {t.watchActive}</span>}
        </div>
        <div className="row">
          <button type="button" className="btn btn-primary" disabled={state.capturing} onClick={() => void state.captureNow()}>
            📸 {state.capturing ? t.captureBusy : t.captureNow}
          </button>
          <button type="button" className={`btn ${state.watching ? 'btn-danger' : 'btn-secondary'}`} onClick={() => void state.toggleWatch()}>
            {state.watching ? `⏹ ${t.watchStop}` : `🎥 ${t.watchStart}`}
          </button>
          <button type="button" className="btn btn-secondary" disabled={state.sending} onClick={() => void state.sendToTelegram()}>
            📱 {state.sending ? t.sendingTelegram : t.sendTelegram}
          </button>
        </div>
      </div>

      <div className="stack">
        <div className="viewport">
          {selected ? (
            <>
              <button type="button" className="viewport-button" aria-label={t.enlarge} onClick={() => setZoom(true)}>
                <img src={captureImageUrl(selected.id)} alt={t.captureAlt} />
              </button>
              <div className="meta-bar">
                <span>
                  <strong>👤 {selected.username}</strong> • ⏰ {selected.timestamp}
                </span>
                <span className="muted">{meta(selected)}</span>
              </div>
            </>
          ) : (
            <div className="empty">{t.capturePlaceholder}</div>
          )}
        </div>

        <div>
          <div className="legend">
            <span>{t.recentCaptures}</span>
            <span className="muted">{t.captureCount(recent.length)}</span>
          </div>
          <div className="thumbs">
            {recent.length === 0 ? (
              <span className="muted">{t.noCaptures}</span>
            ) : (
              recent.map((c) => (
                <button
                  key={c.id}
                  type="button"
                  className="thumb"
                  aria-pressed={selected?.id === c.id}
                  title={`${c.username} @ ${c.timestamp} (${c.width}×${c.height})`}
                  onClick={() => state.select(c)}
                >
                  <img src={captureImageUrl(c.id)} alt={`${t.captureAlt} ${c.timestamp}`} />
                  <span>{c.timestamp}</span>
                </button>
              ))
            )}
          </div>
        </div>
      </div>

      {zoom && selected && (
        <Modal title={`${selected.username} • ${selected.timestamp}`} onClose={() => setZoom(false)} size="full">
          <div className="lightbox">
            <img src={captureImageUrl(selected.id)} alt={t.captureAlt} />
          </div>
        </Modal>
      )}
    </section>
  )
}
