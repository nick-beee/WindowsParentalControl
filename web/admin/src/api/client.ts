// The typed client for the admin routes (/api/admin/*). The API does not change with the SPA.
import type {
  AppRule,
  AppRuleInput,
  Capture,
  Categories,
  Category,
  Dashboard,
  DashboardQuery,
  LoginResult,
  PromptSend,
  Schedule,
  ScheduleExceptionNew,
  ScheduleSave,
  Settings,
  SettingsSave,
  UpdateApply,
  UpdateCheck,
} from './types'

/** An admin route refused the request or failed. `message` is the `error` text of the server, if any. */
export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/** The page path. A reload of it lets the service select the view (login or app) again. */
export const ADMIN_PAGE = '/admin'

let onUnauthorized: () => void = () => window.location.assign(ADMIN_PAGE)

/** Sets what happens when an admin route answers 401 (default: reload /admin). Tests replace it. */
export function setUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler
}

type Method = 'GET' | 'POST' | 'PUT' | 'DELETE'

async function readJson(res: Response): Promise<unknown> {
  const text = await res.text()
  if (!text) return null
  try {
    return JSON.parse(text) as unknown
  } catch {
    return null
  }
}

function errorText(body: unknown): string | null {
  if (body && typeof body === 'object' && 'error' in body) {
    const error = (body as { error: unknown }).error
    if (typeof error === 'string' && error.trim() !== '') return error
  }
  return null
}

function messageText(body: unknown): string | null {
  if (body && typeof body === 'object' && 'message' in body) {
    const message = (body as { message: unknown }).message
    if (typeof message === 'string' && message.trim() !== '') return message
  }
  return null
}

async function request<T>(method: Method, path: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: { Accept: 'application/json' } }
  if (body !== undefined) {
    init.headers = { ...init.headers, 'Content-Type': 'application/json' }
    init.body = JSON.stringify(body)
  }
  const res = await fetch(path, init)
  if (res.status === 401) {
    onUnauthorized()
    throw new ApiError(401, 'HTTP 401')
  }
  const data = await readJson(res)
  if (!res.ok) throw new ApiError(res.status, errorText(data) ?? `HTTP ${res.status}`)
  return data as T
}

function query(params: Record<string, string | number>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) search.set(key, String(value))
  return search.toString()
}

/** The URL of a capture image. The client builds it from the numeric id, not from a server string. */
export function captureImageUrl(id: number): string {
  return `/api/admin/captures/${encodeURIComponent(String(id))}`
}

interface Success {
  success: boolean
}

export const api = {
  // ── Sign-in ──

  /**
   * Sends a TOTP code. A redirect means success. A refused code gives 401 (wrong code or not set up), 403 (a login
   * from the session of a Restricted User) or 429 (too many failed logins), each with a JSON body
   * `{ error, message, blockedUntil }`.
   */
  async login(code: string): Promise<LoginResult> {
    const res = await fetch('/api/admin/login', {
      method: 'POST',
      credentials: 'same-origin',
      redirect: 'manual',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ code }),
    })
    if (res.type === 'opaqueredirect' || res.redirected || (res.status >= 300 && res.status < 400)) return { outcome: 'ok' }
    const outcome = res.status === 403 ? 'refused' : res.status === 429 ? 'blocked' : 'invalid'
    const message = messageText(await readJson(res))
    return message ? { outcome, message } : { outcome }
  },

  /** Ends the Admin Session. The logout is a POST (require-admin-sign-in). */
  async logout(): Promise<void> {
    await fetch('/api/admin/logout', { method: 'POST', credentials: 'same-origin', redirect: 'manual' })
  },

  // ── Dashboard and sessions ──

  getDashboard: (q: DashboardQuery) => request<Dashboard>('GET', `/api/admin/dashboard?${query({ ...q })}`),
  grantMinutes: (userId: number, minutes: number) => request<Success>('POST', '/api/admin/grant', { userId, minutes }),
  forceLogoff: (sessionId: number) => request<Success>('POST', '/api/admin/force-logoff', { sessionId }),
  /** The server waits `seconds` (5 to 60) with a warning before it locks the session. */
  lockSession: (sessionId: number, seconds: number) =>
    request<Success & { message?: string }>('POST', '/api/admin/lock-session', { sessionId, seconds }),
  sendPrompt: (prompt: PromptSend) => request<Success & { promptId?: number }>('POST', '/api/admin/prompt/send', prompt),
  resolveRequest: (requestId: number, action: 'approve' | 'decline', minutes: number) =>
    request<Success>('POST', '/api/admin/resolve-request', { requestId, action, minutes }),

  // ── Screen captures ──

  requestCapture: (username: string) =>
    request<Success>('POST', '/api/admin/capture/request', { username, mode: 'single' }),
  setWatchMode: (username: string, enableWatch: boolean) =>
    request<Success>('POST', '/api/admin/capture/request', { username, mode: 'watch', enableWatch }),
  watchHeartbeat: (username: string) => request<Success>('POST', `/api/admin/capture/heartbeat?${query({ user: username })}`),
  getLatestCapture: (user: string) =>
    request<{ capture: Capture | null }>('GET', `/api/admin/captures/latest?${query({ user })}`),
  getRecentCaptures: (user: string, limit = 10) =>
    request<{ captures: Capture[] }>('GET', `/api/admin/captures/recent?${query({ user, limit })}`),
  sendCaptureToTelegram: (id: number) => request<Success>('POST', `/api/admin/capture/telegram?${query({ id })}`),

  // ── Schedules ──

  getSchedule: (userId: number) => request<Schedule>('GET', `/api/admin/schedule?${query({ userId })}`),
  saveSchedule: (schedule: ScheduleSave) => request<Success & { message?: string }>('POST', '/api/admin/schedule', schedule),
  addScheduleException: (exception: ScheduleExceptionNew) =>
    request<Success>('POST', '/api/admin/schedule-exception', exception),
  deleteScheduleException: (id: number) => request<Success>('DELETE', `/api/admin/schedule-exception?${query({ id })}`),

  // ── Update ──

  checkUpdate: () => request<UpdateCheck>('GET', '/api/admin/update/check'),
  /** The service ignores the body: it reads the latest release itself and runs only a checked installer. */
  applyUpdate: () => request<UpdateApply>('POST', '/api/admin/update/apply'),

  // ── Settings ──

  getSettings: () => request<Settings>('GET', '/api/admin/settings'),
  saveSettings: (settings: SettingsSave) => request<Success>('POST', '/api/admin/settings', settings),

  // ── App Categories and App Rules ──

  getCategories: () => request<Categories>('GET', '/api/admin/categories'),
  setCategoryLimit: (category: Category, weekdayMinutes: number | null, weekendMinutes: number | null) =>
    request<Success>('PUT', '/api/admin/categories', { category, weekdayMinutes, weekendMinutes }),
  getAppRules: () => request<{ rules: AppRule[] }>('GET', '/api/admin/app-rules'),
  addAppRule: (rule: AppRuleInput) => request<Success & { id: number }>('POST', '/api/admin/app-rules', rule),
  updateAppRule: (id: number, rule: AppRuleInput) => request<Success>('PUT', `/api/admin/app-rules/${id}`, rule),
  deleteAppRule: (id: number) => request<Success>('DELETE', `/api/admin/app-rules/${id}`),
}

export type Api = typeof api
