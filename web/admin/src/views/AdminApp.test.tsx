import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setUnauthorizedHandler } from '../api/client'
import { t } from '../i18n'
import { categories, dashboard, rules, XSS } from '../test/fixtures'
import { mockFetch, type Call } from '../test/fetchMock'
import { AdminApp } from './AdminApp'

function routes(overrides: Record<string, unknown> = {}) {
  return {
    'GET /api/admin/dashboard': dashboard(),
    'GET /api/admin/categories': categories,
    'GET /api/admin/app-rules': { rules },
    'GET /api/admin/captures/latest': { capture: null },
    'GET /api/admin/captures/recent': { captures: [] },
    'GET /api/admin/breaks': [
      { userId: 7, username: 'Kid', enabled: true, useMinutes: 45, breakMinutes: 10, continuousUseMinutes: 12, breakEndsAt: null },
    ],
    ...overrides,
  }
}

function last(calls: Call[], key: string): Call | undefined {
  return calls.filter((c) => `${c.method} ${c.path}` === key).at(-1)
}

describe('AdminApp', () => {
  beforeEach(() => setUnauthorizedHandler(vi.fn()))

  it('shows sessions, Grace Requests, users and app usage from the dashboard', async () => {
    mockFetch(routes())
    render(<AdminApp version="v1.6.0" />)
    const sessions = await screen.findByRole('region', { name: /Aktive Sitzungen/ })
    expect(await within(sessions).findByText('minecraft.exe — Minecraft')).toBeInTheDocument()
    expect(within(sessions).getByText(t.remaining(45, 120))).toBeInTheDocument()
    expect(within(sessions).getByText(t.adminBadge)).toBeInTheDocument()
    const requests = screen.getByRole('region', { name: /Anfragen/ })
    expect(within(requests).getByText('Hausaufgaben')).toBeInTheDocument()
    expect(within(requests).getByText(t.requestStatus('DECLINED'))).toBeInTheDocument()
    expect(screen.getByText(t.customDays(2))).toBeInTheDocument()
    expect(screen.getByText('roblox')).toBeInTheDocument()
    expect(screen.getByText('v1.6.0')).toBeInTheDocument()
    const breaks = screen.getByRole('region', { name: new RegExp(t.breaksTitle) })
    expect(await within(breaks).findByText(t.breakUse(12, 45))).toBeInTheDocument()
  })

  it('renders untrusted text as text, not as HTML', async () => {
    const data = dashboard()
    data.sessions[0]!.username = XSS
    data.requests[0]!.reason = XSS
    data.appUsage[0]!.windowTitle = XSS
    mockFetch(routes({ 'GET /api/admin/dashboard': data }))
    const { container } = render(<AdminApp version="" />)
    expect((await screen.findAllByText(XSS)).length).toBeGreaterThanOrEqual(3)
    expect(container.querySelector('img[src="x"]')).toBeNull()
  })

  it('grants bonus minutes after the confirmation', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/grant': { success: true } }))
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<AdminApp version="" />)
    await userEvent.click(await screen.findByRole('button', { name: t.actionGrant(30) }))
    expect(last(calls, 'POST /api/admin/grant')?.body).toEqual({ userId: 7, minutes: 30 })
  })

  it('does not grant when the parent cancels', async () => {
    const calls = mockFetch(routes())
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<AdminApp version="" />)
    await userEvent.click(await screen.findByRole('button', { name: t.actionGrant(15) }))
    expect(last(calls, 'POST /api/admin/grant')).toBeUndefined()
  })

  it('approves a Grace Request with its minutes', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/resolve-request': { success: true } }))
    render(<AdminApp version="" />)
    await userEvent.click(await screen.findByRole('button', { name: t.approve }))
    expect(last(calls, 'POST /api/admin/resolve-request')?.body).toEqual({ requestId: 11, action: 'approve', minutes: 30 })
  })

  it('locks a session with the selected warning time', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/lock-session': { success: true, message: 'ok' } }))
    render(<AdminApp version="" />)
    const kidRow = (await screen.findByText(t.sessionLabel(2))).closest('tr')!
    await userEvent.click(within(kidRow).getByRole('button', { name: /Sperren/ }))
    const dialog = screen.getByRole('dialog')
    await userEvent.selectOptions(within(dialog).getByLabelText(t.lockDelayLabel), '5')
    await userEvent.click(within(dialog).getByRole('button', { name: t.lockConfirm }))
    expect(last(calls, 'POST /api/admin/lock-session')?.body).toEqual({ sessionId: 2, seconds: 5 })
  })

  it('sends an Interactive Session Prompt', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/prompt/send': { success: true, promptId: 1 } }))
    render(<AdminApp version="" />)
    const kidRow = (await screen.findByText(t.sessionLabel(2))).closest('tr')!
    await userEvent.click(within(kidRow).getByRole('button', { name: /Nachricht/ }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText(t.promptMessageLabel), 'Essen!')
    await userEvent.selectOptions(within(dialog).getByLabelText(t.promptUrgencyLabel), 'URGENT')
    await userEvent.click(within(dialog).getByRole('button', { name: /Senden/ }))
    expect(last(calls, 'POST /api/admin/prompt/send')?.body).toEqual({
      username: 'Kid',
      message: 'Essen!',
      urgency: 'URGENT',
      targetDisplay: -1,
      customOptions: null,
    })
    expect(await screen.findByText(t.promptSent('Kid'))).toBeInTheDocument()
  })

  it('assigns an app of the usage table to a category with a new file rule', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/app-rules': { success: true, id: 4 } }))
    render(<AdminApp version="" />)
    await userEvent.selectOptions(await screen.findByLabelText(t.assignCategory('roblox')), 'Games')
    expect(last(calls, 'POST /api/admin/app-rules')?.body).toEqual({ matchType: 'file', pattern: 'roblox.exe', category: 'Games' })
  })

  it('reloads the dashboard with the user and date filters', async () => {
    const calls = mockFetch(routes())
    render(<AdminApp version="" />)
    await userEvent.click(await screen.findByRole('button', { name: t.pastWeek }))
    await userEvent.click(screen.getByRole('button', { name: 'Kid' }))
    const query = last(calls, 'GET /api/admin/dashboard')!.search
    expect(query.get('range')).toBe('week')
    expect(query.get('user')).toBe('Kid')
  })

  it('requests a screen capture of a session', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/capture/request': { success: true } }))
    render(<AdminApp version="" />)
    const kidRow = (await screen.findByText(t.sessionLabel(2))).closest('tr')!
    await userEvent.click(within(kidRow).getByRole('button', { name: /Bildschirm/ }))
    expect(last(calls, 'POST /api/admin/capture/request')?.body).toEqual({ username: 'Kid', mode: 'single' })
  })

  it('shows the latest capture from the client-built URL', async () => {
    const capture = { id: 9, userId: 7, username: 'Kid', timestamp: '2026-11-26 15:00:00', width: 1920, height: 1080, fileSizeBytes: 204800, triggerType: 'MANUAL', url: 'javascript:alert(1)' }
    mockFetch(routes({ 'GET /api/admin/captures/latest': { capture }, 'GET /api/admin/captures/recent': { captures: [capture] } }))
    render(<AdminApp version="" />)
    const images = await screen.findAllByAltText(t.captureAlt)
    expect(images[0]).toHaveAttribute('src', '/api/admin/captures/9')
  })

  it('logs out and reloads the page', async () => {
    const calls = mockFetch(routes({ 'POST /api/admin/logout': () => new Response('', { status: 302 }) }))
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    render(<AdminApp version="" />)
    await screen.findByText(t.sessionLabel(2))
    await userEvent.click(screen.getByRole('button', { name: `🚪 ${t.logout}` }))
    expect(last(calls, 'POST /api/admin/logout')).toBeDefined()
    expect(assign).toHaveBeenCalledWith('/admin')
  })
})
