// The client for POST /api/request (Grace Request of the Restricted User). The route does not change with the SPA.

export interface ExtensionRequest {
  requestType: 'extension'
  minutes: number
  reason: string
  username: string
}

export interface ScheduleChangeRequest {
  requestType: 'schedule_change'
  targetDate: string
  minutes: number
  requestedStart: string
  requestedEnd: string
  reason: string
  username: string
}

/**
 * - sent: the service accepted the request; `message` is its text in the preset.
 * - refused: the service refused it; `error` is its text, or null.
 * - unreachable: no answer, or an answer without JSON (for example 503 when the PC is offline).
 */
export type SendResult =
  | { kind: 'sent'; message: string }
  | { kind: 'refused'; error: string | null }
  | { kind: 'unreachable' }

export async function sendRequest(body: ExtensionRequest | ScheduleChangeRequest): Promise<SendResult> {
  let res: Response
  let data: unknown
  try {
    res = await fetch('/api/request', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    })
    data = JSON.parse(await res.text()) as unknown
  } catch {
    return { kind: 'unreachable' }
  }
  const fields = data && typeof data === 'object' ? (data as Record<string, unknown>) : {}
  if (res.ok && fields.success === true) return { kind: 'sent', message: String(fields.message ?? '') }
  const error = typeof fields.error === 'string' && fields.error !== '' ? fields.error : null
  return { kind: 'refused', error }
}
