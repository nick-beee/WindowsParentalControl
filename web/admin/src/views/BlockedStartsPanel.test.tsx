import { fireEvent, render, screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { setUnauthorizedHandler } from '../api/client'
import type { BlockedStart } from '../api/types'
import { formatClock } from '../format'
import { t } from '../i18n'
import { json, mockFetch, type Call } from '../test/fetchMock'
import { BlockedStartsPanel } from './BlockedStartsPanel'

const SCRIPT_PATH = 'C:\\Users\\Kid\\<script>window.__xss=1</script>\\chrome.exe'

function start(overrides: Partial<BlockedStart> = {}): BlockedStart {
  return {
    username: 'Kid',
    fileName: 'chrome.exe',
    exePath: 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    closedCount: 3,
    suspendedCount: 1,
    firstAt: '2026-11-26T13:02:03Z',
    lastAt: '2026-11-26T15:04:05Z',
    ...overrides,
  }
}

function lastGet(calls: Call[]): Call | undefined {
  return calls.filter((c) => c.path === '/api/admin/blocked-starts').at(-1)
}

beforeEach(() => setUnauthorizedHandler(vi.fn()))

describe('BlockedStartsPanel', () => {
  it('shows per program and day the counts and the first and last time', async () => {
    const calls = mockFetch({ 'GET /api/admin/blocked-starts': { date: '2026-11-26', starts: [start()] } })
    render(<BlockedStartsPanel tick={0} />)
    const row = (await screen.findByText('chrome.exe')).closest('tr')!
    const cells = within(row).getAllByRole('cell').map((c) => c.textContent)
    expect(cells).toEqual([
      'Kid',
      'chrome.exe',
      '3',
      '1',
      formatClock('2026-11-26T13:02:03Z'),
      formatClock('2026-11-26T15:04:05Z'),
      'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    ])
    // Without a date, the server answers with its own today.
    expect(lastGet(calls)?.search.has('date')).toBe(false)
    expect(screen.getByLabelText(t.blockedStartsDate)).toHaveValue('2026-11-26')
  })

  it('shows a path with <script> as text', async () => {
    mockFetch({ 'GET /api/admin/blocked-starts': { date: '2026-11-26', starts: [start({ exePath: SCRIPT_PATH, fileName: '<b>x</b>.exe' })] } })
    const { container } = render(<BlockedStartsPanel tick={0} />)
    expect(await screen.findByText(SCRIPT_PATH)).toBeInTheDocument()
    expect(screen.getByText('<b>x</b>.exe')).toBeInTheDocument()
    expect(container.querySelector('script')).toBeNull()
    expect(container.querySelector('b')).toBeNull()
    expect((window as unknown as { __xss?: number }).__xss).toBeUndefined()
  })

  it('loads another day', async () => {
    const calls = mockFetch({
      'GET /api/admin/blocked-starts': (call: Call) =>
        json({ date: call.search.get('date') ?? '2026-11-26', starts: call.search.has('date') ? [] : [start()] }),
    })
    render(<BlockedStartsPanel tick={0} />)
    await screen.findByText('chrome.exe')
    fireEvent.change(screen.getByLabelText(t.blockedStartsDate), { target: { value: '2026-11-25' } })
    expect(await screen.findByText(t.blockedStartsEmpty)).toBeInTheDocument()
    expect(lastGet(calls)?.search.get('date')).toBe('2026-11-25')
  })

  it('shows the server error', async () => {
    mockFetch({ 'GET /api/admin/blocked-starts': () => json({ error: 'date must be yyyy-MM-dd.' }, 400) })
    render(<BlockedStartsPanel tick={0} />)
    expect(await screen.findByText(t.blockedStartsLoadFailed('date must be yyyy-MM-dd.'))).toBeInTheDocument()
  })
})
