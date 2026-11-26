import { CATEGORIES, type Category } from '../api/types'
import { t } from '../i18n'

interface Props {
  value: Category | null
  onChange: (value: Category | null) => void
  /** Adds an empty first option (no file rule). */
  withEmpty?: boolean
  label: string
  id?: string
}

export function CategorySelect({ value, onChange, withEmpty, label, id }: Props) {
  return (
    <select
      id={id}
      className="input input-sm"
      aria-label={label}
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value === '' ? null : (e.target.value as Category))}
    >
      {withEmpty && <option value="">{t.noFileRule}</option>}
      {CATEGORIES.map((c) => (
        <option key={c} value={c}>
          {t.categoryName(c)}
        </option>
      ))}
    </select>
  )
}
