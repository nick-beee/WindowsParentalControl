import type { RequestDay, RequestPageData } from '../request/data'

/**
 * Test texts. The real texts are in RequestPageTexts.cs on the server, so the tests do not copy them: each text
 * here names its key, and the texts with a placeholder keep it.
 */
export const texts: Record<string, string> = {
  heading: 'T:heading',
  account: 'T:account',
  session_inactive: 'T:session_inactive',
  session_locked: 'T:session_locked',
  session_active: 'T:session_active',
  timer_label: 'T:timer_label',
  timer_curfew: 'T:timer_curfew',
  timer_over: '(T:over)',
  offline: 'T:offline',
  status_pending: 'T:pending',
  status_approved: 'T:approved',
  status_declined: 'T:declined',
  requests_disabled: 'T:requests_disabled',
  request_pending: 'T:request_pending (Status: {status}).',
  request_max_reached: 'T:request_max_reached 3 (Status: {status}).',
  request_used: 'T:request_used 1 of 3 (Status: {status}).',
  decline_title: 'T:decline_title (18:05)',
  schedule_title: 'T:schedule_title',
  schedule_default: 'T:schedule_default',
  dot_exception: 'T:dot_exception',
  dot_custom: 'T:dot_custom',
  badge_standard: 'T:badge_standard',
  badge_custom: 'T:badge_custom',
  badge_exception: 'T:badge_exception',
  today_suffix: '(T:today)',
  metric_quota: 'T:metric_quota',
  metric_window: 'T:metric_window',
  tab_extension: 'T:tab_extension',
  tab_schedule: 'T:tab_schedule',
  ext_choose: 'T:ext_choose',
  ext_reason_label: 'T:ext_reason_label',
  ext_reason_placeholder: 'T:ext_reason_placeholder',
  ext_submit: 'T:ext_submit',
  ext_sending: 'T:ext_sending',
  ext_failed: 'T:ext_failed',
  sched_date: 'T:sched_date',
  sched_minutes: 'T:sched_minutes',
  sched_start: 'T:sched_start',
  sched_end: 'T:sched_end',
  sched_reason_label: 'T:sched_reason_label',
  sched_reason_placeholder: 'T:sched_reason_placeholder',
  sched_submit: 'T:sched_submit',
  sched_sending: 'T:sched_sending',
  sched_failed: 'T:sched_failed',
  connect_failed: 'T:connect_failed',
}

const CODES = ['Mo', 'Tu', 'We', 'Th', 'Fr', 'Sa', 'Su']

function day(i: number, overrides: Partial<RequestDay> = {}): RequestDay {
  return {
    dayName: `Day${i}`,
    dayCode: CODES[i] ?? `D${i}`,
    minutesText: `${60 + i} min`,
    timeWindow: '08:00 – 20:00',
    isCustom: false,
    isToday: false,
    hasException: false,
    exceptionNote: null,
    ...overrides,
  }
}

export function requestData(overrides: Partial<RequestPageData> = {}): RequestPageData {
  return {
    lang: 'de',
    username: 'Kid',
    remainingSeconds: 3725,
    ticking: true,
    curfew: '08:00 – 20:00',
    isOffline: false,
    requestsToday: 0,
    maxRequests: 3,
    hasPending: false,
    latestStatus: null,
    declineReason: null,
    sessionState: 'active',
    days: [
      day(0),
      day(1, { isCustom: true, minutesText: '90 min (custom)' }),
      day(2, { isToday: true, hasException: true, exceptionNote: 'T:exception_today' }),
      day(3),
      day(4),
      day(5),
      day(6),
    ],
    extensionOptions: [
      { minutes: 15, label: '+15 min' },
      { minutes: 30, label: '+30 min' },
      { minutes: 60, label: '+1 h' },
    ],
    scheduleOptions: [60, 90, 120, 150, 180, 240].map((m) => ({ minutes: m, label: `${m} min` })),
    dates: { min: '2026-11-26', tomorrow: '2026-11-27', max: '2026-12-03' },
    texts,
    ...overrides,
  }
}
