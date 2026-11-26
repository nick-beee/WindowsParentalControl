import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mockFetch, json } from '../test/fetchMock'
import { ApiError, api, captureImageUrl, setUnauthorizedHandler } from './client'

function redirectResponse(): Response {
  // fetch with redirect: 'manual' gives an opaque redirect (status 0) in a browser.
  return { type: 'opaqueredirect', redirected: false, status: 0, ok: false } as Response
}

describe('api.login', () => {
  it('maps a redirect to success', async () => {
    mockFetch({ 'POST /api/admin/login': () => redirectResponse() })
    await expect(api.login('123456')).resolves.toEqual({ outcome: 'ok' })
  })

  it.each([
    [401, 'invalid'], // wrong code, or not set up
    [403, 'refused'], // login from the session of a Restricted User
    [429, 'blocked'], // too many failed logins
  ] as const)('maps status %i to %s', async (status, outcome) => {
    mockFetch({ 'POST /api/admin/login': () => new Response('', { status }) })
    await expect(api.login('123456')).resolves.toEqual({ outcome })
  })

  it('returns the text of the service, for example the end of a block', async () => {
    const message = 'Zu viele falsche Codes. Die Anmeldung von diesem Gerät ist gesperrt bis 26.11.2026, 18:30 Uhr.'
    mockFetch({
      'POST /api/admin/login': () => json({ error: 'blocked', message, blockedUntil: '26.11.2026, 18:30 Uhr' }, 429),
    })
    await expect(api.login('123456')).resolves.toEqual({ outcome: 'blocked', message })
  })

  it('ignores an answer that is not JSON', async () => {
    mockFetch({ 'POST /api/admin/login': () => new Response('<html></html>', { status: 401 }) })
    await expect(api.login('123456')).resolves.toEqual({ outcome: 'invalid' })
  })

  it('sends the code as JSON with redirect manual', async () => {
    const calls = mockFetch({ 'POST /api/admin/login': () => redirectResponse() })
    await api.login('654321')
    expect(calls[0]?.body).toEqual({ code: '654321' })
    expect(vi.mocked(fetch).mock.calls[0]?.[1]?.redirect).toBe('manual')
  })
})

describe('api.logout', () => {
  it('sends one POST and no GET', async () => {
    const calls = mockFetch({ 'POST /api/admin/logout': () => new Response('', { status: 302 }) })
    await api.logout()
    expect(calls.map((c) => c.method)).toEqual(['POST'])
  })
})

describe('request', () => {
  const unauthorized = vi.fn()
  beforeEach(() => setUnauthorizedHandler(unauthorized))
  afterEach(() => unauthorized.mockReset())

  it('calls the 401 handler and throws', async () => {
    mockFetch({ 'GET /api/admin/settings': () => new Response('', { status: 401 }) })
    await expect(api.getSettings()).rejects.toMatchObject({ status: 401 })
    expect(unauthorized).toHaveBeenCalledOnce()
  })

  it('turns the error text of the server into the message', async () => {
    mockFetch({ 'POST /api/admin/app-rules': () => json({ error: 'Rule exists.' }, 409) })
    const error = await api.addAppRule({ matchType: 'file', pattern: 'a.exe', category: 'Games' }).catch((e: unknown) => e)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).message).toBe('Rule exists.')
    expect((error as ApiError).status).toBe(409)
    expect(unauthorized).not.toHaveBeenCalled()
  })

  it('accepts an error without a body', async () => {
    mockFetch({ 'POST /api/admin/resolve-request': () => new Response('', { status: 404 }) })
    await expect(api.resolveRequest(1, 'approve', 15)).rejects.toThrow('HTTP 404')
  })

  it('encodes the dashboard query', async () => {
    const calls = mockFetch({ 'GET /api/admin/dashboard': {} })
    await api.getDashboard({ user: 'Kid & Co', date: '2026-11-26', range: 'week' })
    expect(calls[0]?.search.get('user')).toBe('Kid & Co')
    expect(calls[0]?.search.get('range')).toBe('week')
  })
})

describe('request bodies', () => {
  it('sends the lock delay as seconds (the field the service reads)', async () => {
    const calls = mockFetch({ 'POST /api/admin/lock-session': { success: true } })
    await api.lockSession(4, 30)
    expect(calls[0]?.body).toEqual({ sessionId: 4, seconds: 30 })
  })

  it('sends no body to update/apply', async () => {
    const calls = mockFetch({ 'POST /api/admin/update/apply': { success: true, message: 'ok' } })
    await api.applyUpdate()
    expect(calls[0]?.body).toBeUndefined()
  })

  it('sends the capture modes of the old page', async () => {
    const calls = mockFetch({ 'POST /api/admin/capture/request': { success: true } })
    await api.requestCapture('Kid')
    await api.setWatchMode('Kid', false)
    expect(calls.map((c) => c.body)).toEqual([
      { username: 'Kid', mode: 'single' },
      { username: 'Kid', mode: 'watch', enableWatch: false },
    ])
  })

  it('deletes a schedule exception by query id', async () => {
    const calls = mockFetch({ 'DELETE /api/admin/schedule-exception': { success: true } })
    await api.deleteScheduleException(9)
    expect(calls[0]?.search.get('id')).toBe('9')
  })
})

it('builds the capture image URL from the id', () => {
  expect(captureImageUrl(42)).toBe('/api/admin/captures/42')
})
