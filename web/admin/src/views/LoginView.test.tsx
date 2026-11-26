import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { App } from '../App'
import { t } from '../i18n'
import { json, mockFetch } from '../test/fetchMock'
import { LoginView } from './LoginView'

describe('LoginView', () => {
  it('signs in with a valid code', async () => {
    mockFetch({ 'POST /api/admin/login': () => ({ type: 'opaqueredirect', status: 0 }) as Response })
    const onSignedIn = vi.fn()
    render(<LoginView onSignedIn={onSignedIn} />)
    await userEvent.type(screen.getByLabelText(t.loginCodeLabel), '123456')
    await userEvent.click(screen.getByRole('button', { name: t.loginButton }))
    expect(onSignedIn).toHaveBeenCalledOnce()
  })

  it('shows the text for a wrong code and clears the field', async () => {
    mockFetch({ 'POST /api/admin/login': () => new Response('', { status: 401 }) })
    render(<LoginView onSignedIn={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(t.loginCodeLabel), '000000')
    await userEvent.click(screen.getByRole('button', { name: t.loginButton }))
    expect(await screen.findByRole('alert')).toHaveTextContent(t.loginInvalid)
    expect(screen.getByLabelText(t.loginCodeLabel)).toHaveValue('')
  })

  it('shows the block text for status 429', async () => {
    mockFetch({ 'POST /api/admin/login': () => new Response('', { status: 429 }) })
    render(<LoginView onSignedIn={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(t.loginCodeLabel), '111111')
    await userEvent.click(screen.getByRole('button', { name: t.loginButton }))
    expect(await screen.findByRole('alert')).toHaveTextContent(t.loginBlocked)
  })

  it('shows the text of the service with the end of the block', async () => {
    const message = 'Zu viele falsche Codes. Die Anmeldung von diesem Gerät ist gesperrt bis 26.11.2026, 18:30 Uhr.'
    mockFetch({ 'POST /api/admin/login': () => json({ error: 'blocked', message, blockedUntil: '26.11.2026, 18:30 Uhr' }, 429) })
    render(<LoginView onSignedIn={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(t.loginCodeLabel), '111111')
    await userEvent.click(screen.getByRole('button', { name: t.loginButton }))
    expect(await screen.findByRole('alert')).toHaveTextContent('gesperrt bis 26.11.2026, 18:30 Uhr')
  })

  it('shows a text of the service as text, not as HTML', async () => {
    mockFetch({ 'POST /api/admin/login': () => json({ error: 'invalid', message: '<img src=x onerror=alert(1)>' }, 401) })
    const { container } = render(<LoginView onSignedIn={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(t.loginCodeLabel), '000000')
    await userEvent.click(screen.getByRole('button', { name: t.loginButton }))
    expect(await screen.findByRole('alert')).toHaveTextContent('<img src=x onerror=alert(1)>')
    expect(container.querySelector('img')).toBeNull()
  })

  it('checks the format before it sends', async () => {
    const calls = mockFetch({})
    render(<LoginView onSignedIn={vi.fn()} />)
    await userEvent.type(screen.getByLabelText(t.loginCodeLabel), '12a3')
    await userEvent.click(screen.getByRole('button', { name: t.loginButton }))
    expect(screen.getByRole('alert')).toHaveTextContent(t.loginFormat)
    expect(calls).toHaveLength(0)
  })
})

describe('App views', () => {
  it('shows only the setup text before the setup (no form)', () => {
    const { container } = render(<App view="setup" version="" />)
    expect(screen.getByText(t.setupText)).toBeInTheDocument()
    expect(container.querySelector('form, input, button')).toBeNull()
  })

  it('shows the refusal text for the refused view', () => {
    render(<App view="refused" version="" />)
    expect(screen.getByRole('alert')).toHaveTextContent(t.loginRefused)
  })

  // A request to an admin route gets 401, and a 401 reloads /admin: the setup and refused views must send none.
  it.each(['setup', 'refused', 'login'] as const)('sends no request in the %s view', async (view) => {
    const calls = mockFetch({})
    render(<App view={view} version="" />)
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(calls).toHaveLength(0)
  })

  it('shows the login form for the login view', () => {
    render(<App view="login" version="" />)
    expect(screen.getByLabelText(t.loginCodeLabel)).toBeInTheDocument()
  })
})
