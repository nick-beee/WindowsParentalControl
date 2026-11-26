import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setUnauthorizedHandler } from '../api/client'
import type { UpdateCheck } from '../api/types'
import { NoticeProvider } from '../components/notices'
import { t } from '../i18n'
import { mockFetch, type Call } from '../test/fetchMock'
import { categories, rules, schedule, XSS } from '../test/fixtures'
import { CategoriesPanel } from '../views/CategoriesPanel'
import { LOCK_DELAYS } from './LockDialog'
import { ScheduleDialog, applyPreset } from './ScheduleDialog'
import { SettingsDialog } from './SettingsDialog'
import { UpdateDialog, releaseNoteLines } from './UpdateDialog'

function last(calls: Call[], key: string): Call | undefined {
  return calls.filter((c) => `${c.method} ${c.path}` === key).at(-1)
}

beforeEach(() => setUnauthorizedHandler(vi.fn()))

describe('CategoriesPanel', () => {
  function renderPanel(reload = vi.fn(async () => {})) {
    render(
      <NoticeProvider>
        <CategoriesPanel categories={categories} rules={rules} loadError={null} reload={reload} reloadDashboard={vi.fn()} />
      </NoticeProvider>,
    )
    return reload
  }

  it('saves a Category Limit; an empty field means no limit', async () => {
    const calls = mockFetch({ 'PUT /api/admin/categories': { success: true } })
    const reload = renderPanel()
    const weekday = screen.getByLabelText(`${t.categoryName('Games')} ${t.colWeekday}`)
    await userEvent.clear(weekday)
    await userEvent.click(screen.getByRole('button', { name: `${t.categoryName('Games')} ${t.save}` }))
    expect(last(calls, 'PUT /api/admin/categories')?.body).toEqual({ category: 'Games', weekdayMinutes: null, weekendMinutes: 120 })
    expect(reload).toHaveBeenCalled()
  })

  it('does not let the parent limit System', () => {
    mockFetch({})
    renderPanel()
    expect(screen.getByLabelText(`${t.categoryName('System')} ${t.colWeekday}`)).toBeDisabled()
    expect(screen.queryByRole('button', { name: `${t.categoryName('System')} ${t.save}` })).toBeNull()
    expect(screen.getByText('Kid: 25 Min.')).toBeInTheDocument()
  })

  it('changes an App Rule with its App Limit', async () => {
    const calls = mockFetch({ 'PUT /api/admin/app-rules/3': { success: true } })
    renderPanel()
    await userEvent.selectOptions(screen.getByLabelText(`${t.colCategory}: Minecraft.exe`), 'Creative')
    await userEvent.type(screen.getByLabelText(`${t.colAppLimitWeekend}: Minecraft.exe`), '150')
    await userEvent.click(screen.getByRole('button', { name: t.saveRule('Minecraft.exe') }))
    expect(last(calls, 'PUT /api/admin/app-rules/3')?.body).toEqual({
      matchType: 'file',
      pattern: 'Minecraft.exe',
      category: 'Creative',
      weekdayMinutes: 90,
      weekendMinutes: 150,
    })
  })

  it('shows the server error when a new rule is refused', async () => {
    mockFetch({ 'POST /api/admin/app-rules': () => new Response(JSON.stringify({ error: 'A rule for this file exists.' }), { status: 409 }) })
    renderPanel()
    await userEvent.type(screen.getByLabelText(t.colPattern), 'minecraft.exe')
    await userEvent.click(screen.getByRole('button', { name: /Regel hinzufügen/ }))
    expect(await screen.findByRole('alert')).toHaveTextContent('A rule for this file exists.')
  })

  it('deletes a rule after the confirmation', async () => {
    const calls = mockFetch({ 'DELETE /api/admin/app-rules/3': { success: true } })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderPanel()
    await userEvent.click(screen.getByRole('button', { name: t.deleteRule('Minecraft.exe') }))
    expect(last(calls, 'DELETE /api/admin/app-rules/3')).toBeDefined()
  })
})

describe('ScheduleDialog', () => {
  it('applies the school preset to Monday to Friday only', () => {
    const days = applyPreset(schedule.days, 'school', { minutes: '120', start: '08:00', end: '20:00' })
    expect(days.filter((d) => d.isCustom).map((d) => d.dayOfWeek)).toEqual([1, 2, 3, 4, 5])
    expect(days[0]).toMatchObject({ dailyMinutes: 90, scheduleStart: '15:00', scheduleEnd: '20:00' })
  })

  it('shows German day names and saves the week', async () => {
    const calls = mockFetch({ 'GET /api/admin/schedule': schedule, 'POST /api/admin/schedule': { success: true } })
    const onSaved = vi.fn()
    render(
      <NoticeProvider>
        <ScheduleDialog userId={7} username="Kid" onClose={vi.fn()} onSaved={onSaved} />
      </NoticeProvider>,
    )
    expect(await screen.findByText('Montag')).toBeInTheDocument()
    expect(screen.getByText('2026-12-24')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Wochenende/ }))
    await userEvent.click(screen.getByRole('button', { name: /Zeitplan speichern/ }))
    const body = last(calls, 'POST /api/admin/schedule')?.body as { days: { dayOfWeek: number; isCustom: boolean }[] }
    expect(body).toMatchObject({ userId: 7, baseDailyMinutes: 120, baseScheduleStart: '08:00', baseScheduleEnd: '20:00' })
    expect(body.days.filter((d) => d.isCustom).map((d) => d.dayOfWeek)).toEqual([6, 0])
    expect(onSaved).toHaveBeenCalled()
  })

  it('refuses a window that ends before it starts', async () => {
    const calls = mockFetch({ 'GET /api/admin/schedule': schedule })
    render(
      <NoticeProvider>
        <ScheduleDialog userId={7} username="Kid" onClose={vi.fn()} onSaved={vi.fn()} />
      </NoticeProvider>,
    )
    const end = await screen.findByLabelText(t.baseEnd)
    await userEvent.clear(end)
    await userEvent.type(end, '07:00')
    await userEvent.click(screen.getByRole('button', { name: /Zeitplan speichern/ }))
    expect(screen.getByRole('alert')).toHaveTextContent(t.invalidBaseWindow)
    expect(last(calls, 'POST /api/admin/schedule')).toBeUndefined()
  })

  it('adds a schedule exception', async () => {
    const calls = mockFetch({ 'GET /api/admin/schedule': schedule, 'POST /api/admin/schedule-exception': { success: true } })
    render(
      <NoticeProvider>
        <ScheduleDialog userId={7} username="Kid" onClose={vi.fn()} onSaved={vi.fn()} />
      </NoticeProvider>,
    )
    const date = await screen.findByLabelText(t.exceptionDate)
    await userEvent.clear(date)
    await userEvent.type(date, '2026-12-31')
    await userEvent.click(screen.getByRole('button', { name: /Ausnahme hinzufügen/ }))
    expect(last(calls, 'POST /api/admin/schedule-exception')?.body).toEqual({
      userId: 7,
      date: '2026-12-31',
      dailyMinutes: 120,
      scheduleStart: '08:00',
      scheduleEnd: '22:00',
    })
  })
})

describe('UpdateDialog', () => {
  const update: UpdateCheck = {
    currentVersion: 'v1.6.0',
    latestVersion: '1.7.0',
    hasUpdate: true,
    releaseTitle: 'v1.7.0',
    releaseNotes: `## What's Changed\n* feat: new ${XSS}\n\n**Full Changelog**: x`,
    downloadUrl: 'https://example.invalid/setup.exe',
    error: null,
  }

  it('formats the release notes as plain lines', () => {
    expect(releaseNoteLines(update.releaseNotes)).toEqual([`• feat: new ${XSS}`])
  })

  it('shows the new version and installs it without a request body', async () => {
    const calls = mockFetch({ 'GET /api/admin/update/check': update, 'POST /api/admin/update/apply': { success: true, message: 'läuft' } })
    const onChecked = vi.fn()
    const { container } = render(
      <NoticeProvider>
        <UpdateDialog onClose={vi.fn()} onChecked={onChecked} />
      </NoticeProvider>,
    )
    expect(await screen.findByText(`• feat: new ${XSS}`)).toBeInTheDocument()
    expect(container.ownerDocument.querySelector('img[src="x"]')).toBeNull()
    expect(onChecked).toHaveBeenCalledWith(update)
    await userEvent.click(screen.getByRole('button', { name: /installieren/ }))
    expect(last(calls, 'POST /api/admin/update/apply')?.body).toBeUndefined()
  })

  it('shows the error of the check', async () => {
    mockFetch({ 'GET /api/admin/update/check': { ...update, hasUpdate: false, error: 'GitHub nicht erreichbar' } })
    render(
      <NoticeProvider>
        <UpdateDialog onClose={vi.fn()} onChecked={vi.fn()} />
      </NoticeProvider>,
    )
    expect(await screen.findByText(t.updateFailed('GitHub nicht erreichbar'))).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /installieren/ })).toBeNull()
  })
})

describe('SettingsDialog', () => {
  it('loads and saves the settings', async () => {
    const calls = mockFetch({
      'GET /api/admin/settings': {
        telegramConfigured: false,
        telegramNotifySignIn: true,
        telegramNotifySignOut: false,
        telegramNotifyAdminLogon: true,
        promptResponsePresets: 'Ja\nNein',
        msgCategoryWarn: 'Noch {minutes} Minuten {name}',
        msgCategorySuspended: '{name} pausiert',
        telegramNotifyBreak: false,
        msgBreakWarn: 'In {minutes} Minuten ist Pause.',
        msgBreakStart: 'Pause bis {break_end}',
        msgBreakRunning: 'Noch {minutes} Minuten bis {break_end}',
      },
      'POST /api/admin/settings': { success: true },
    })
    const onClose = vi.fn()
    render(
      <NoticeProvider>
        <SettingsDialog onClose={onClose} />
      </NoticeProvider>,
    )
    const dialog = await screen.findByRole('dialog')
    expect(await within(dialog).findByText(/Telegram sind noch nicht eingerichtet/)).toBeInTheDocument()
    await userEvent.click(within(dialog).getByLabelText(t.notifySignOut))
    await userEvent.click(within(dialog).getByLabelText(t.notifyBreak))
    const start = within(dialog).getByLabelText(t.msgBreakStartLabel)
    await userEvent.clear(start)
    await userEvent.type(start, 'Pause bis {{break_end} Uhr')
    await userEvent.click(within(dialog).getByRole('button', { name: /Speichern/ }))
    expect(last(calls, 'POST /api/admin/settings')?.body).toEqual({
      telegramNotifySignIn: true,
      telegramNotifySignOut: true,
      telegramNotifyAdminLogon: true,
      promptResponsePresets: 'Ja\nNein',
      msgCategoryWarn: 'Noch {minutes} Minuten {name}',
      msgCategorySuspended: '{name} pausiert',
      telegramNotifyBreak: true,
      msgBreakWarn: 'In {minutes} Minuten ist Pause.',
      msgBreakStart: 'Pause bis {break_end} Uhr',
      msgBreakRunning: 'Noch {minutes} Minuten bis {break_end}',
    })
    expect(onClose).toHaveBeenCalled()
  })
})

it('offers only lock delays that the service accepts (5 to 60 seconds)', () => {
  expect(LOCK_DELAYS.every((s) => s >= 5 && s <= 60)).toBe(true)
})
