import { useEffect, useId, type ReactNode } from 'react'
import { t } from '../i18n'

interface ModalProps {
  title: ReactNode
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  size?: 'normal' | 'wide' | 'full'
}

/** A dialog over the page. Escape and a click on the backdrop close it. */
export function Modal({ title, onClose, children, footer, size = 'normal' }: ModalProps) {
  const titleId = useId()
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className={`modal-box modal-${size}`} role="dialog" aria-modal="true" aria-labelledby={titleId}>
        <div className="modal-head">
          <h3 id={titleId}>{title}</h3>
          <button type="button" className="btn btn-link" aria-label={t.close} onClick={onClose}>
            ✕
          </button>
        </div>
        {children}
        {footer && <div className="modal-foot">{footer}</div>}
      </div>
    </div>
  )
}
