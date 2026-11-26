import type { AppRule, Categories, Dashboard, Schedule } from '../api/types'

export const XSS = '<img src=x onerror="window.__xss=1">'

export function dashboard(overrides: Partial<Dashboard> = {}): Dashboard {
  return {
    sessions: [
      {
        sessionId: 2,
        username: 'Kid',
        userId: 7,
        isRestricted: true,
        isLocked: false,
        remainingMinutes: 45,
        totalAllowed: 120,
        curfew: '08:00–20:00',
        currentApp: 'minecraft.exe — Minecraft',
        isAppLive: true,
      },
      {
        sessionId: 1,
        username: 'Parent',
        userId: 1,
        isRestricted: false,
        isLocked: false,
        remainingMinutes: 0,
        totalAllowed: 0,
        curfew: 'None',
        currentApp: 'Inactive / Idle',
        isAppLive: false,
      },
    ],
    requests: [
      { id: 11, username: 'Kid', requestedMinutes: 30, reason: 'Hausaufgaben', status: 'PENDING', createdAt: '15:01:02' },
      { id: 10, username: 'Kid', requestedMinutes: 15, reason: 'Spiel', status: 'DECLINED', createdAt: '14:00:00' },
    ],
    managedUsers: [{ id: 7, username: 'Kid', baseMinutes: 120, baseStart: '08:00', baseEnd: '20:00', customCount: 2 }],
    appUsage: [
      {
        fileRuleCategory: null,
        userId: 7,
        username: 'Kid',
        processName: 'roblox',
        windowTitle: 'Roblox',
        minutes: 30,
        percentage: 60,
      },
    ],
    hourlyTimeline: Array.from({ length: 24 }, (_, h) => (h === 15 ? 30 : 0)),
    totalMinutes: 50,
    availableUsers: ['Kid'],
    selectedUser: 'all',
    selectedDate: '2026-11-26',
    selectedRange: 'day',
    ...overrides,
  }
}

export const categories: Categories = {
  categories: [
    { category: 'Games', weekdayMinutes: 60, weekendMinutes: 120, editable: true },
    { category: 'System', weekdayMinutes: null, weekendMinutes: null, editable: false },
  ],
  usageToday: [{ username: 'Kid', category: 'Games', minutes: 25 }],
  maxMinutes: 1440,
}

export const rules: AppRule[] = [
  { id: 3, matchType: 'file', pattern: 'Minecraft.exe', category: 'Games', weekdayMinutes: 90, weekendMinutes: null, isDefault: false },
]

export const schedule: Schedule = {
  userId: 7,
  username: 'Kid',
  baseDailyMinutes: 120,
  baseScheduleStart: '08:00',
  baseScheduleEnd: '20:00',
  days: [1, 2, 3, 4, 5, 6, 0].map((d) => ({
    dayOfWeek: d,
    dayName: 'x',
    isCustom: false,
    dailyMinutes: 120,
    scheduleStart: '08:00',
    scheduleEnd: '20:00',
  })),
  upcomingExceptions: [{ id: 5, date: '2026-12-24', dailyMinutes: 240, scheduleStart: '08:00', scheduleEnd: '22:00' }],
}
