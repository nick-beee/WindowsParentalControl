// The JSON of the admin routes in src/ParentalControl.Service/Web/WebServerHost.cs.
// Text fields can come from the child's PC (exe names, window titles, user names, reasons).
// React renders them as text; never render them as HTML.

/** The App Categories of the service (AppCategories.All). The API uses these English names. */
export const CATEGORIES = ['Games', 'Video', 'Social', 'Study', 'Creative', 'System', 'Unknown'] as const
export type Category = (typeof CATEGORIES)[number]

export type DateRange = 'day' | 'week'

export interface DashboardQuery {
  user: string
  date: string
  range: DateRange
}

export interface Session {
  sessionId: number
  username: string
  userId: number
  isRestricted: boolean
  isLocked: boolean
  remainingMinutes: number
  totalAllowed: number
  /** "HH:mm–HH:mm", or "None" without a limit. */
  curfew: string
  /** "process — title", the process name, or "Inactive / Idle". */
  currentApp: string
  isAppLive: boolean
}

export type GraceRequestStatus = 'PENDING' | 'APPROVED' | 'DECLINED'

export interface GraceRequest {
  id: number
  username: string
  requestedMinutes: number
  reason: string | null
  status: GraceRequestStatus | string
  createdAt: string
}

export interface ManagedUser {
  id: number
  username: string
  baseMinutes: number
  baseStart: string
  baseEnd: string
  customCount: number
}

export interface AppUsage {
  fileRuleCategory: Category | null
  userId: number
  username: string
  processName: string
  windowTitle: string | null
  minutes: number
  percentage: number
}

export interface Dashboard {
  sessions: Session[]
  requests: GraceRequest[]
  managedUsers: ManagedUser[]
  appUsage: AppUsage[]
  /** 24 values: the minutes of each hour. */
  hourlyTimeline: number[]
  totalMinutes: number
  availableUsers: string[]
  selectedUser: string
  selectedDate: string
  selectedRange: DateRange
}

export interface Capture {
  id: number
  userId: number
  username: string
  timestamp: string
  date?: string
  width: number
  height: number
  fileSizeBytes: number
  triggerType: string
  url: string
}

export interface ScheduleDay {
  /** 0 = Sunday … 6 = Saturday (System.DayOfWeek). */
  dayOfWeek: number
  dayName: string
  isCustom: boolean
  dailyMinutes: number
  scheduleStart: string
  scheduleEnd: string
}

export interface ScheduleException {
  id: number
  date: string
  dailyMinutes: number
  scheduleStart: string
  scheduleEnd: string
}

export interface Schedule {
  userId: number
  username: string
  baseDailyMinutes: number
  baseScheduleStart: string
  baseScheduleEnd: string
  days: ScheduleDay[]
  upcomingExceptions: ScheduleException[]
}

export interface ScheduleSave {
  userId: number
  baseDailyMinutes: number
  baseScheduleStart: string
  baseScheduleEnd: string
  days: ScheduleDay[]
}

export interface ScheduleExceptionNew {
  userId: number
  date: string
  dailyMinutes: number
  scheduleStart: string
  scheduleEnd: string
}

export interface UpdateCheck {
  currentVersion: string
  latestVersion: string | null
  hasUpdate: boolean
  releaseTitle: string | null
  releaseNotes: string | null
  downloadUrl: string | null
  error: string | null
}

export interface UpdateApply {
  success: boolean
  message: string | null
}

export interface Settings {
  telegramConfigured: boolean
  telegramNotifySignIn: boolean
  telegramNotifySignOut: boolean
  telegramNotifyAdminLogon: boolean
  promptResponsePresets: string
  msgCategoryWarn: string
  msgCategorySuspended: string
  /** Telegram notice at each Break start (usage-breaks). Default off. */
  telegramNotifyBreak: boolean
  /** Child texts of the Breaks. An empty text keeps the old one on the server. */
  msgBreakWarn: string
  msgBreakStart: string
  msgBreakRunning: string
}

export type SettingsSave = Omit<Settings, 'telegramConfigured'>

/** The ranges of a Break Rule (BreakRule in the Domain). The PUT route refuses other values with 400. */
export const BREAK_USE_MINUTES = { min: 10, max: 240 } as const
export const BREAK_BREAK_MINUTES = { min: 5, max: 120 } as const

/** One item of GET /api/admin/breaks: the Break Rule and the Break state of one Restricted User. */
export interface BreakStatus {
  userId: number
  username: string
  enabled: boolean
  /** N: the minutes of Continuous Use before a Break. */
  useMinutes: number
  /** M: the length of a Break in minutes. */
  breakMinutes: number
  continuousUseMinutes: number
  /** The end of the running Break as an ISO 8601 time in UTC, or null if no Break runs. */
  breakEndsAt: string | null
}

export interface BreakRuleInput {
  enabled: boolean
  useMinutes: number
  breakMinutes: number
}

export interface CategoryLimitRow {
  category: Category
  weekdayMinutes: number | null
  weekendMinutes: number | null
  editable: boolean
}

export interface CategoryUsage {
  username: string
  category: Category
  minutes: number
}

export interface Categories {
  categories: CategoryLimitRow[]
  usageToday: CategoryUsage[]
  maxMinutes: number
}

export type RuleMatchType = 'file' | 'folder'

export interface AppRule {
  id: number
  matchType: RuleMatchType
  pattern: string
  category: Category
  weekdayMinutes: number | null
  weekendMinutes: number | null
  isDefault: boolean
}

export interface AppRuleInput {
  matchType: RuleMatchType
  pattern: string
  category: Category
  weekdayMinutes?: number | null
  weekendMinutes?: number | null
}

export type PromptUrgency = 'NORMAL' | 'URGENT'

export interface PromptSend {
  username: string
  message: string
  urgency: PromptUrgency
  targetDisplay: number
  customOptions: string | null
}

/** The outcome of a login attempt, from the status code of the answer. */
export type LoginOutcome = 'ok' | 'invalid' | 'refused' | 'blocked'

/**
 * The result of a login attempt. For a refused code, the service sends the German text of AdminAccessTexts
 * (`message`); for a block, the text names the time when the block ends.
 */
export interface LoginResult {
  outcome: LoginOutcome
  message?: string
}
