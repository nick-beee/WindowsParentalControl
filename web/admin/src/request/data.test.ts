import { describe, expect, it } from 'vitest'
import { readRequestData, translator } from './data'

function page(content: string | null): Document {
  const doc = document.implementation.createHTMLDocument('x')
  if (content !== null) {
    const meta = doc.createElement('meta')
    meta.name = 'pc-request-data'
    meta.content = content
    doc.head.append(meta)
  }
  return doc
}

describe('readRequestData', () => {
  it('reads the JSON of the meta tag', () => {
    const data = readRequestData(page(JSON.stringify({ username: '<b>Kid</b>', texts: { heading: 'H' } })))
    expect(data?.username).toBe('<b>Kid</b>')
    expect(data?.texts.heading).toBe('H')
  })

  it('reads the HTML-encoded attribute that the service writes', () => {
    const doc = new DOMParser().parseFromString(
      '<meta name="pc-request-data" content="{&quot;texts&quot;:{},&quot;declineReason&quot;:&quot;&lt;/script&gt; &amp; &#39;x&#39;&quot;}">',
      'text/html',
    )
    expect(readRequestData(doc)?.declineReason).toBe("</script> & 'x'")
  })

  it('gives null without data', () => {
    expect(readRequestData(page(null))).toBeNull()
    expect(readRequestData(page('__PC_REQUEST_DATA__'))).toBeNull()
    expect(readRequestData(page('{not json'))).toBeNull()
    expect(readRequestData(page('{"username":"Kid"}'))).toBeNull()
  })
})

describe('translator', () => {
  it('gives the key for an unknown key, as TextSet does', () => {
    const t = translator({ heading: 'H' })
    expect(t('heading')).toBe('H')
    expect(t('missing_key')).toBe('missing_key')
    expect(t('toString')).toBe('toString')
  })
})
