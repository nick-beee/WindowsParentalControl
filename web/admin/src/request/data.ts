/**
 * The data of the child request page. The service writes it as JSON into the meta tag pc-request-data
 * (RequestSpa.cs, RequestPageModel.cs). The page has no texts of its own: `texts` is the catalog of the Language
 * Preset (RequestPageTexts.cs). The service fills each number and word form; only `{status}` is still open.
 */
export interface RequestDay {
  dayName: string
  dayCode: string
  minutesText: string
  timeWindow: string
  isCustom: boolean
  isToday: boolean
  hasException: boolean
  exceptionNote: string | null
}

export interface RequestOption {
  minutes: number
  label: string
}

/** The banner for a parent who opens the page of a Restricted User. `text` has the placeholder `{user}`. */
export interface AdminTest {
  title: string
  text: string
  switchLabel: string
  /** The users for the switch links. Empty if there is only one Restricted User. */
  users: string[]
}

export type SessionState = 'inactive' | 'locked' | 'active'

export interface RequestPageData {
  lang: string
  username: string
  remainingSeconds: number
  /** True if the session runs and is not locked: only then the countdown goes down. */
  ticking: boolean
  curfew: string
  isOffline: boolean
  requestsToday: number
  maxRequests: number
  hasPending: boolean
  /** PENDING, APPROVED or DECLINED from the database, or null. */
  latestStatus: string | null
  /** The reason of the parent for the last decline, else null. Text of the parent: show it as text only. */
  declineReason: string | null
  adminTest: AdminTest | null
  sessionState: SessionState
  days: RequestDay[]
  extensionOptions: RequestOption[]
  scheduleOptions: RequestOption[]
  dates: { min: string; tomorrow: string; max: string }
  texts: Record<string, string>
}

/** The data of the page, or null if the meta tag is missing or has no valid JSON (for example in `vite dev`). */
export function readRequestData(doc: Document = document): RequestPageData | null {
  const content = doc.querySelector<HTMLMetaElement>('meta[name="pc-request-data"]')?.content
  if (!content || content.startsWith('__')) return null
  try {
    const data = JSON.parse(content) as unknown
    return data && typeof data === 'object' && 'texts' in data ? (data as RequestPageData) : null
  } catch {
    return null
  }
}

/** The text of a key. An unknown key gives the key itself, so that the error is visible (as TextSet does). */
export type Translate = (key: string) => string

export function translator(texts: Record<string, string>): Translate {
  return (key) => (Object.hasOwn(texts, key) ? (texts[key] ?? key) : key)
}
