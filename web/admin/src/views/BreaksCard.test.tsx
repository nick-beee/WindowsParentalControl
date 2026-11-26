import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setUnauthorizedHandler } from '../api/client'
import type { BreakStatus } from '../api/types'
import { NoticeProvider } from '../components/notices'
import { clockTime } from '../format'
import { t } from '../i18n'
import { json, mockFetch, type Call } from '../test/fetchMock'
import { XSS } from '../test/fixtures'
import { BreaksCard, isValidBreakRule } from './BreaksCard'

const END = '2026-11-26T14:40:00.0000000Z'

function item(overrides: Partial<BreakStatus> = {}): BreakStatus {
  return {
    userId: 7,
    username: 'Kid',
    enabled: true,
    useMinutes: 45,
    breakMinutes: 10,
    continuousUseMinutes: 31,
    breakEndsAt: null,
    ...overrides,
  }
}

function last(calls: Call[], key: string): Call | undefined {
  return calls.filter((c) => `${c.method} ${c.path}` === key).at(-1)
}

function renderCard(tick = 0) {
  return render(
    <NoticeProvider>
      <BreaksCard tick={tick} />
    </NoticeProvider>,
  )
}

beforeEach(() => setUnauthorizedHandler(vi.fn()))

describe('BreaksCard', () => {
  it('shows the rule and the Continuous Use of each Restricted User', async () => {
    mockFetch({ 'GET /api/admin/breaks': [item()] })
    renderCard()
    const card = await screen.findByRole('region', { name: new RegExp(t.breaksTitle) })
    expect(await within(card).findByText(t.breakUse(31, 45))).toBeInTheDocument()
    expect(within(card).getByLabelText(t.breakOnLabel('Kid'))).toBeChecked()
    expect(within(card).getByLabelText(t.useMinutesLabel('Kid'))).toHaveValue(45)
    expect(within(card).getByLabelText(t.breakMinutesLabel('Kid'))).toHaveValue(10)
    expect(within(card).queryByRole('button', { name: t.breakEndLabel('Kid') })).toBeNull()
  })

  it('shows a running Break with its local end time and an end button', async () => {
    mockFetch({ 'GET /api/admin/breaks': [item({ continuousUseMinutes: 0, breakEndsAt: END })] })
    renderCard()
    expect(await screen.findByText(new RegExp(t.breakRunning(clockTime(END))))).toBeInTheDocument()
    expect(screen.getByRole('button', { name: t.breakEndLabel('Kid') })).toBeInTheDocument()
  })

  it('shows a rule that is off', async () => {
    mockFetch({ 'GET /api/admin/breaks': [item({ enabled: false })] })
    renderCard()
    expect(await screen.findByText(t.breakOff)).toBeInTheDocument()
    expect(screen.getByLabelText(t.breakOnLabel('Kid'))).not.toBeChecked()
  })

  it('saves the rule with all three fields', async () => {
    const calls = mockFetch({
      'GET /api/admin/breaks': [item()],
      'PUT /api/admin/breaks/7': item({ useMinutes: 30, breakMinutes: 5, enabled: false }),
    })
    renderCard()
    const useField = await screen.findByLabelText(t.useMinutesLabel('Kid'))
    await userEvent.clear(useField)
    await userEvent.type(useField, '30')
    const breakField = screen.getByLabelText(t.breakMinutesLabel('Kid'))
    await userEvent.clear(breakField)
    await userEvent.type(breakField, '5')
    await userEvent.click(screen.getByLabelText(t.breakOnLabel('Kid')))
    await userEvent.click(screen.getByRole('button', { name: t.breakSave('Kid') }))
    expect(last(calls, 'PUT /api/admin/breaks/7')?.body).toEqual({ enabled: false, useMinutes: 30, breakMinutes: 5 })
    expect(await screen.findByText(t.breakRuleSaved('Kid'))).toBeInTheDocument()
    expect(calls.filter((c) => c.method === 'GET').length).toBeGreaterThanOrEqual(2)
  })

  it('sends no request for a value out of range', async () => {
    const calls = mockFetch({ 'GET /api/admin/breaks': [item()] })
    renderCard()
    const useField = await screen.findByLabelText(t.useMinutesLabel('Kid'))
    await userEvent.clear(useField)
    await userEvent.type(useField, '5')
    await userEvent.click(screen.getByRole('button', { name: t.breakSave('Kid') }))
    expect(await screen.findByText(t.breakRangeError(10, 240, 5, 120))).toBeInTheDocument()
    expect(calls.some((c) => c.method === 'PUT')).toBe(false)
  })

  it('shows the error text of the server if the PUT fails', async () => {
    mockFetch({
      'GET /api/admin/breaks': [item()],
      'PUT /api/admin/breaks/7': () => json({ error: 'useMinutes must be from 10 to 240' }, 400),
    })
    renderCard()
    await userEvent.click(await screen.findByRole('button', { name: t.breakSave('Kid') }))
    expect(await screen.findByText(t.notSaved('useMinutes must be from 10 to 240'))).toBeInTheDocument()
  })

  it('ends a running Break after the confirmation', async () => {
    let running = true
    const calls = mockFetch({
      'GET /api/admin/breaks': () => json([item({ continuousUseMinutes: 0, breakEndsAt: running ? END : null })]),
      'POST /api/admin/breaks/7/end': () => {
        running = false
        return json({ ended: true })
      },
    })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderCard()
    await userEvent.click(await screen.findByRole('button', { name: t.breakEndLabel('Kid') }))
    expect(confirm).toHaveBeenCalledWith(t.confirmEndBreak('Kid'))
    expect(last(calls, 'POST /api/admin/breaks/7/end')).toBeDefined()
    expect(await screen.findByText(t.breakEnded('Kid'))).toBeInTheDocument()
    expect(await screen.findByText(t.breakUse(0, 45))).toBeInTheDocument()
  })

  it('sends nothing if the parent cancels the confirmation', async () => {
    const calls = mockFetch({ 'GET /api/admin/breaks': [item({ breakEndsAt: END })] })
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderCard()
    await userEvent.click(await screen.findByRole('button', { name: t.breakEndLabel('Kid') }))
    expect(calls.some((c) => c.method === 'POST')).toBe(false)
  })

  it('says so if the Break already ended (409)', async () => {
    mockFetch({
      'GET /api/admin/breaks': [item({ breakEndsAt: END })],
      'POST /api/admin/breaks/7/end': () => json({ error: 'No Break of this user runs.' }, 409),
    })
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    renderCard()
    await userEvent.click(await screen.findByRole('button', { name: t.breakEndLabel('Kid') }))
    expect(await screen.findByText(t.breakNotRunning)).toBeInTheDocument()
  })

  it('loads the state again when the tick changes', async () => {
    const calls = mockFetch({ 'GET /api/admin/breaks': [item()] })
    const view = renderCard(0)
    await screen.findByText(t.breakUse(31, 45))
    view.rerender(
      <NoticeProvider>
        <BreaksCard tick={1} />
      </NoticeProvider>,
    )
    await vi.waitFor(() => expect(calls.filter((c) => c.path === '/api/admin/breaks').length).toBe(2))
  })

  it('shows a load error and an empty list', async () => {
    mockFetch({ 'GET /api/admin/breaks': () => json({ error: 'kaputt' }, 500) })
    renderCard()
    expect(await screen.findByText(`${t.breaksLoadFailed} kaputt`)).toBeInTheDocument()
  })

  it('renders the user name as text, not as HTML', async () => {
    mockFetch({ 'GET /api/admin/breaks': [item({ username: XSS })] })
    const { container } = renderCard()
    expect(await screen.findByText(XSS)).toBeInTheDocument()
    expect(container.querySelector('img[src="x"]')).toBeNull()
  })
})

describe('isValidBreakRule', () => {
  it('accepts the limits of the ranges and refuses values outside', () => {
    expect(isValidBreakRule(10, 5)).toBe(true)
    expect(isValidBreakRule(240, 120)).toBe(true)
    expect(isValidBreakRule(9, 5)).toBe(false)
    expect(isValidBreakRule(241, 5)).toBe(false)
    expect(isValidBreakRule(10, 4)).toBe(false)
    expect(isValidBreakRule(10, 121)).toBe(false)
    expect(isValidBreakRule(10.5, 5)).toBe(false)
    expect(isValidBreakRule(Number.NaN, 5)).toBe(false)
  })
})

describe('clockTime', () => {
  it('formats the local time as HH:mm', () => {
    const local = new Date(2026, 10, 26, 9, 5)
    expect(clockTime(local.toISOString())).toBe('09:05')
  })
})
