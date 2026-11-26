import { vi } from 'vitest'

export interface Call {
  method: string
  path: string
  search: URLSearchParams
  body: unknown
}

type Handler = (call: Call) => Response | Promise<Response>

export function json(data: unknown, status = 200): Response {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } })
}

/**
 * Replaces fetch with a router: keys are "METHOD /path" (no query). A route without a handler answers 404.
 * Returns the list of calls.
 */
export function mockFetch(routes: Record<string, unknown | Handler>): Call[] {
  const calls: Call[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost')
      const method = (init?.method ?? 'GET').toUpperCase()
      const body = typeof init?.body === 'string' ? (JSON.parse(init.body) as unknown) : init?.body
      const call: Call = { method, path: url.pathname, search: url.searchParams, body }
      calls.push(call)
      const route = routes[`${method} ${url.pathname}`]
      if (route === undefined) return new Response('', { status: 404 })
      if (typeof route === 'function') return (route as Handler)(call)
      return json(route)
    }),
  )
  return calls
}
