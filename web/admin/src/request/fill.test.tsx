import { render } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { fillNodes } from './fill'

function paragraph(text: string, values: Parameters<typeof fillNodes>[1]): HTMLElement {
  return render(<p>{fillNodes(text, values)}</p>).container.querySelector('p')!
}

describe('fillNodes', () => {
  it('puts a node into the placeholder', () => {
    const p = paragraph('Status: {status}.', { status: <strong>OK</strong> })
    expect(p).toHaveTextContent('Status: OK.')
    expect(p.querySelector('strong')).toHaveTextContent('OK')
  })

  it('keeps a placeholder without a value', () => {
    expect(paragraph('{a} and {b}', { a: 'A' })).toHaveTextContent('A and {b}')
  })

  it('does not fill a value again', () => {
    expect(paragraph('{a}-{b}', { a: '{b}', b: 'B' })).toHaveTextContent('{b}-B')
  })

  it('shows HTML in the text and in the values as text', () => {
    const p = paragraph('<i>{a}</i>', { a: '<img src=x>' })
    expect(p).toHaveTextContent('<i><img src=x></i>')
    expect(p.querySelector('i, img')).toBeNull()
  })
})
