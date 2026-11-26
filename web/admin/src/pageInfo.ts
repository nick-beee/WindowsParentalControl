/**
 * The view that the service selected for this request (meta tag pc-admin-view, set in AdminSpa.cs):
 * - setup: the sign-in is not set up; show only the setup text (require-admin-sign-in, #21).
 * - login: show the login form.
 * - refused: a login from this Windows account is not possible (#21, status 403).
 * - app: the admin has a valid Admin Session.
 * A missing or unknown value gives the login view (fail closed).
 */
export type PageView = 'setup' | 'login' | 'refused' | 'app'

const VIEWS: readonly PageView[] = ['setup', 'login', 'refused', 'app']

function meta(name: string, doc: Document): string | null {
  return doc.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.content ?? null
}

export function readPageView(doc: Document = document): PageView {
  const value = meta('pc-admin-view', doc)
  return VIEWS.includes(value as PageView) ? (value as PageView) : 'login'
}

/** The version of the service, for example "v1.6.0". Empty if the page has no value. */
export function readVersion(doc: Document = document): string {
  const value = meta('pc-version', doc) ?? ''
  return value.startsWith('__') ? '' : value
}
