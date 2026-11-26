import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { App } from '../App'
import { t } from '../i18n'
import { mockFetch } from '../test/fetchMock'
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

  it('shows the login form for the login view', () => {
    render(<App view="login" version="" />)
    expect(screen.getByLabelText(t.loginCodeLabel)).toBeInTheDocument()
  })
})
