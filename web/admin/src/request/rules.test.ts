import { describe, expect, it } from 'vitest'
import { requestData, texts } from '../test/requestFixture'
import { translator } from './data'
import { formatCountdown, requestLimit, statusLabel } from './rules'

const t = translator(texts)

describe('requestLimit', () => {
  it('has no banner for the first request', () => {
    expect(requestLimit(requestData(), t)).toMatchObject({ key: null, disabled: false })
  })

  it('blocks the form when the PC is offline', () => {
    expect(requestLimit(requestData({ isOffline: true }), t)).toMatchObject({ key: null, disabled: true })
    expect(requestLimit(requestData({ isOffline: true, requestsToday: 1 }), t)).toMatchObject({ key: 'request_used', disabled: true })
  })

  it('uses the order of the old page', () => {
    const all = { maxRequests: 0, hasPending: true, requestsToday: 5 }
    expect(requestLimit(requestData(all), t)).toMatchObject({ key: 'requests_disabled', kind: 'error', disabled: true })
    expect(requestLimit(requestData({ ...all, maxRequests: 3 }), t)).toMatchObject({ key: 'request_pending', status: 'T:pending', disabled: true })
    expect(requestLimit(requestData({ ...all, maxRequests: 3, hasPending: false, latestStatus: 'APPROVED' }), t)).toMatchObject({
      key: 'request_max_reached',
      status: 'T:approved',
      disabled: true,
    })
    expect(requestLimit(requestData({ requestsToday: 1, latestStatus: 'DECLINED' }), t)).toMatchObject({
      key: 'request_used',
      status: 'T:declined',
      disabled: false,
    })
  })
})

describe('statusLabel', () => {
  it('keeps an unknown status', () => {
    expect(statusLabel('EXPIRED', t)).toBe('EXPIRED')
    expect(statusLabel(null, t)).toBe('')
  })
})

describe('formatCountdown', () => {
  it.each([
    [0, '00:00 (over)'],
    [-5, '00:00 (over)'],
    [59, '00:59'],
    [600, '10:00'],
    [3725, '1:02:05'],
  ])('%i seconds give %s', (seconds, expected) => {
    expect(formatCountdown(seconds, '(over)')).toBe(expected)
  })
})
