import type { RequestPageData, Translate } from './data'

/** The label of a status from the database, in the preset. An unknown status stays as it is. */
export function statusLabel(status: string | null, t: Translate): string {
  switch (status) {
    case 'PENDING':
      return t('status_pending')
    case 'APPROVED':
      return t('status_approved')
    case 'DECLINED':
      return t('status_declined')
    default:
      return status ?? ''
  }
}

/** The banner about the requests of today, and whether the page blocks a new request. */
export interface RequestLimit {
  /** The text key of the banner, or null for no banner. */
  key: 'requests_disabled' | 'request_pending' | 'request_max_reached' | 'request_used' | null
  kind: 'error' | 'info'
  /** The status for the `{status}` placeholder. */
  status: string
  disabled: boolean
}

export function requestLimit(data: RequestPageData, t: Translate): RequestLimit {
  const status = statusLabel(data.latestStatus, t)
  if (data.maxRequests <= 0) return { key: 'requests_disabled', kind: 'error', status, disabled: true }
  if (data.hasPending) return { key: 'request_pending', kind: 'info', status: t('status_pending'), disabled: true }
  if (data.requestsToday >= data.maxRequests) return { key: 'request_max_reached', kind: 'info', status, disabled: true }
  if (data.requestsToday > 0) return { key: 'request_used', kind: 'info', status, disabled: data.isOffline }
  return { key: null, kind: 'info', status, disabled: data.isOffline }
}

/** The countdown text: "mm:ss", "h:mm:ss", or "00:00" and the text for the end of the time. */
export function formatCountdown(totalSeconds: number, overText: string): string {
  if (totalSeconds <= 0) return `00:00 ${overText}`
  const hours = Math.floor(totalSeconds / 3600)
  const mm = String(Math.floor((totalSeconds % 3600) / 60)).padStart(2, '0')
  const ss = String(totalSeconds % 60).padStart(2, '0')
  return hours > 0 ? `${hours}:${mm}:${ss}` : `${mm}:${ss}`
}
