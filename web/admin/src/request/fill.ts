import { Fragment, createElement, type ReactNode } from 'react'

const PLACEHOLDER = /\{([A-Za-z_][A-Za-z0-9_]*)\}/g

/**
 * Puts React nodes into the placeholders of a text, for example `{status}` as bold text. React escapes each part,
 * so a text from the catalog or the database stays text. A placeholder without a value stays in the text, as in
 * TextTemplate.cs. Word forms (`{n|one|other}`) are not supported: the service fills them before it sends the text.
 */
export function fillNodes(text: string, values: Record<string, ReactNode>): ReactNode[] {
  const parts: ReactNode[] = []
  let last = 0
  for (const match of text.matchAll(PLACEHOLDER)) {
    const name = match[1] ?? ''
    if (!Object.hasOwn(values, name)) continue
    if (match.index > last) parts.push(text.slice(last, match.index))
    parts.push(createElement(Fragment, { key: `${name}-${match.index}` }, values[name]))
    last = match.index + match[0].length
  }
  if (last < text.length) parts.push(text.slice(last))
  return parts
}
