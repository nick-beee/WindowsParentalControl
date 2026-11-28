import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { XSS } from '../test/fixtures'
import { json, mockFetch } from '../test/fetchMock'
import { requestData } from '../test/requestFixture'
import { RequestApp } from './RequestApp'

const extForm = () => document.getElementById('formExt')
const schedForm = () => document.getElementById('formSched')

afterEach(() => {
  vi.useRealTimers()
})

describe('RequestApp', () => {
  it('shows the state of the Restricted User with the texts of the service', () => {
    render(<RequestApp data={requestData({ sessionState: 'locked', curfew: '07:00 – 19:00' })} />)
    expect(screen.getByRole('heading')).toHaveTextContent('T:heading')
    expect(screen.getByText('T:session_locked')).toHaveClass('session-badge', 'badge-locked')
    expect(screen.getByText('Kid').closest('.user-badge')).toHaveTextContent('T:account Kid')
    expect(screen.getByText('07:00 – 19:00').closest('.timer-curfew')).toHaveTextContent('T:timer_curfew 07:00 – 19:00')
    expect(screen.getByText('T:schedule_default')).toBeInTheDocument()
    expect(extForm()).toBeVisible()
    expect(schedForm()).not.toBeVisible()
  })

  it('shows the decline reason of the parent as text', () => {
    render(<RequestApp data={requestData({ latestStatus: 'DECLINED', declineReason: XSS })} />)
    expect(screen.getByText('T:decline_title (18:05)')).toBeInTheDocument()
    expect(screen.getByText(`"${XSS}"`)).toHaveClass('decline-text')
    expect(document.querySelector('img')).toBeNull()
  })

  it('shows no decline banner without a reason', () => {
    render(<RequestApp data={requestData({ latestStatus: 'DECLINED' })} />)
    expect(screen.queryByText(/T:decline_title/)).toBeNull()
  })

  it('shows the requests of today with the status in bold', () => {
    render(<RequestApp data={requestData({ requestsToday: 1, latestStatus: 'APPROVED' })} />)
    const banner = screen.getByText(/T:request_used/)
    expect(banner).toHaveTextContent('T:request_used 1 of 3 (Status: T:approved).')
    expect(within(banner).getByText('T:approved').tagName).toBe('STRONG')
    expect(screen.getByRole('button', { name: 'T:ext_submit' })).toBeEnabled()
  })

  it.each([
    ['pending', { hasPending: true }, /T:request_pending/],
    ['the maximum', { requestsToday: 3 }, /T:request_max_reached/],
    ['requests off', { maxRequests: 0 }, /T:requests_disabled/],
    ['offline', { isOffline: true }, /T:offline/],
  ])('blocks the forms for %s', (_, overrides, banner) => {
    render(<RequestApp data={requestData(overrides)} />)
    expect(screen.getByText(banner)).toHaveClass('banner')
    expect(screen.getByRole('button', { name: 'T:ext_submit' })).toBeDisabled()
    expect(screen.getByLabelText('T:ext_reason_label')).toBeDisabled()
    expect(screen.getByLabelText('+15 min')).toBeDisabled()
    expect(screen.getByLabelText('T:sched_date')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'T:sched_submit', hidden: true })).toBeDisabled()
  })

  it('selects today in the day strip and shows another day on a click', async () => {
    render(<RequestApp data={requestData()} />)
    expect(screen.getByRole('button', { name: 'Day2' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByText('Day2 (T:today)')).toBeInTheDocument()
    expect(screen.getByText('• T:exception_today')).toBeInTheDocument()
    expect(screen.getByText('T:badge_exception', { selector: '.badge-tag' })).toHaveClass('exception')
    expect(screen.getByTitle('T:dot_exception')).toHaveTextContent('★')
    expect(screen.getByTitle('T:dot_custom')).toHaveTextContent('•')

    await userEvent.click(screen.getByRole('button', { name: 'Day1' }))
    expect(screen.getByRole('button', { name: 'Day1' })).toHaveClass('day-pill', 'active')
    expect(screen.getByRole('button', { name: 'Day2' })).toHaveClass('today')
    expect(screen.getByRole('button', { name: 'Day2' })).not.toHaveClass('active')
    expect(screen.getByText('Day1')).toHaveClass('card-day-title')
    expect(screen.getByText('90 min (custom)')).toBeInTheDocument()
    expect(screen.getByText('T:badge_custom')).toHaveClass('badge-tag', 'custom')
  })

  it('shows no admin test banner and no links to other users', () => {
    const { container } = render(<RequestApp data={requestData()} />)
    expect(container.querySelector('.banner-admin')).toBeNull()
    expect(container.querySelector('a[href*="user="]')).toBeNull()
  })

  it('sends a request for more time and hides the forms', async () => {
    const calls = mockFetch({ 'POST /api/request': { success: true, message: 'Sent +30.' } })
    render(<RequestApp data={requestData()} />)
    await userEvent.click(screen.getByLabelText('+30 min'))
    await userEvent.type(screen.getByLabelText('T:ext_reason_label'), 'Homework')
    await userEvent.click(screen.getByRole('button', { name: 'T:ext_submit' }))

    expect(await screen.findByRole('status')).toHaveTextContent('✅ Sent +30.')
    expect(calls[0]?.body).toEqual({ requestType: 'extension', minutes: 30, reason: 'Homework' })
    expect(extForm()).not.toBeVisible()
    expect(schedForm()).not.toBeVisible()

    // As on the old page, a tab shows its form again, but the sent form stays blocked.
    await userEvent.click(screen.getByRole('tab', { name: 'T:tab_extension' }))
    expect(extForm()).toBeVisible()
    expect(screen.getByRole('button', { name: 'T:ext_sending' })).toBeDisabled()
  })

  it('does not send without a reason', async () => {
    const calls = mockFetch({})
    render(<RequestApp data={requestData()} />)
    await userEvent.click(screen.getByRole('button', { name: 'T:ext_submit' }))
    expect(calls).toHaveLength(0)
  })

  it('shows the error of the service and allows a new try', async () => {
    mockFetch({ 'POST /api/request': () => json({ error: 'Max reached.' }, 400) })
    render(<RequestApp data={requestData()} />)
    await userEvent.type(screen.getByLabelText('T:ext_reason_label'), 'x')
    await userEvent.click(screen.getByRole('button', { name: 'T:ext_submit' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('❌ Max reached.')
    expect(screen.getByRole('button', { name: 'T:ext_submit' })).toBeEnabled()
    expect(extForm()).toBeVisible()
  })

  it('shows the text of the service for a request without a valid Request Token', async () => {
    mockFetch({ 'POST /api/request': () => json({ error: 'Window expired.' }, 401) })
    render(<RequestApp data={requestData()} />)
    await userEvent.type(screen.getByLabelText('T:ext_reason_label'), 'x')
    await userEvent.click(screen.getByRole('button', { name: 'T:ext_submit' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('❌ Window expired.')
  })

  it.each([
    ['an error without text', () => json({}, 400), '❌ T:ext_failed'],
    ['503 without a body (offline)', () => new Response('', { status: 503 }), '❌ T:connect_failed'],
    ['no connection', () => Promise.reject(new TypeError('Failed to fetch')), '❌ T:connect_failed'],
  ])('shows the right text for %s', async (_, answer, expected) => {
    mockFetch({ 'POST /api/request': answer })
    render(<RequestApp data={requestData()} />)
    await userEvent.type(screen.getByLabelText('T:ext_reason_label'), 'x')
    await userEvent.click(screen.getByRole('button', { name: 'T:ext_submit' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(expected)
  })

  it('sends a schedule change with the default values of the form', async () => {
    const calls = mockFetch({ 'POST /api/request': { success: true, message: 'Plan sent.' } })
    render(<RequestApp data={requestData()} />)
    await userEvent.click(screen.getByRole('tab', { name: 'T:tab_schedule' }))
    expect(schedForm()).toBeVisible()
    expect(extForm()).not.toBeVisible()
    expect(screen.getByLabelText('T:sched_date')).toHaveAttribute('min', '2026-11-26')
    expect(screen.getByLabelText('T:sched_date')).toHaveAttribute('max', '2026-12-03')
    expect(screen.getByLabelText('T:sched_minutes')).toHaveValue('120')
    await userEvent.selectOptions(screen.getByLabelText('T:sched_minutes'), '180 min')
    await userEvent.type(screen.getByLabelText('T:sched_reason_label'), 'Holiday')
    await userEvent.click(screen.getByRole('button', { name: 'T:sched_submit' }))

    expect(await screen.findByRole('status')).toHaveTextContent('✅ Plan sent.')
    expect(calls[0]?.body).toEqual({
      requestType: 'schedule_change',
      targetDate: '2026-11-27',
      minutes: 180,
      requestedStart: '08:00',
      requestedEnd: '21:00',
      reason: 'Holiday',
    })
  })

  it('keeps the typed reason after a tab switch', async () => {
    render(<RequestApp data={requestData()} />)
    await userEvent.type(screen.getByLabelText('T:ext_reason_label'), 'Homework')
    await userEvent.click(screen.getByRole('tab', { name: 'T:tab_schedule' }))
    await userEvent.click(screen.getByRole('tab', { name: 'T:tab_extension' }))
    expect(screen.getByLabelText('T:ext_reason_label')).toHaveValue('Homework')
  })
})

describe('Countdown', () => {
  it('counts down each second and shows the time in the title', () => {
    vi.useFakeTimers()
    render(<RequestApp data={requestData({ remainingSeconds: 2 })} />)
    expect(screen.getByRole('timer')).toHaveTextContent('00:02')
    expect(document.title).toBe('[00:02] Parental Control')
    act(() => vi.advanceTimersByTime(1000))
    expect(screen.getByRole('timer')).toHaveTextContent('00:01')
    act(() => vi.advanceTimersByTime(5000))
    expect(screen.getByRole('timer')).toHaveTextContent('00:00 (T:over)')
    expect(screen.getByRole('timer')).toHaveClass('timer-over')
    expect(document.title).toBe('[00:00 (T:over)] Parental Control')
  })

  it('stops while the session is locked or not active', () => {
    vi.useFakeTimers()
    render(<RequestApp data={requestData({ remainingSeconds: 90, ticking: false })} />)
    act(() => vi.advanceTimersByTime(5000))
    expect(screen.getByRole('timer')).toHaveTextContent('01:30')
    expect(screen.getByRole('timer')).not.toHaveClass('timer-over')
  })
})
