import { describe, expect, it } from 'vitest'
import { readPageView, readVersion } from './pageInfo'

function addMeta(d: Document, name: string, content: string) {
  const meta = d.createElement('meta')
  meta.name = name
  meta.content = content
  d.head.appendChild(meta)
}

function doc(view: string | null, version = 'v1.6.0'): Document {
  const d = document.implementation.createHTMLDocument('')
  if (view !== null) addMeta(d, 'pc-admin-view', view)
  addMeta(d, 'pc-version', version)
  return d
}

describe('readPageView', () => {
  it.each(['setup', 'login', 'refused', 'app'])('reads %s', (view) => {
    expect(readPageView(doc(view))).toBe(view)
  })

  it.each([null, '', '__PC_ADMIN_VIEW__', 'admin'])('fails closed to login for %s', (view) => {
    expect(readPageView(doc(view))).toBe('login')
  })
})

describe('readVersion', () => {
  it('reads the version', () => expect(readVersion(doc('app'))).toBe('v1.6.0'))
  it('ignores the placeholder of a page that the service did not fill', () =>
    expect(readVersion(doc('app', '__PC_VERSION__'))).toBe(''))
})
