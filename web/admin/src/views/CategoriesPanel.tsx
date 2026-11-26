import { useState } from 'react'
import { api } from '../api/client'
import type { AppRule, Categories, Category, CategoryLimitRow, RuleMatchType } from '../api/types'
import { CategorySelect } from '../components/CategorySelect'
import { errorMessage, useNotify } from '../components/notices'
import { formatMinutes, minutesOrNull, minutesText } from '../format'
import { t } from '../i18n'

interface Props {
  categories: Categories | null
  rules: AppRule[] | null
  loadError: string | null
  /** Loads the categories and the rules again. */
  reload: () => Promise<void>
  /** Loads the dashboard again (the App Usage table shows the file rules). */
  reloadDashboard: () => void
}

function LimitRow({ row, usage, reload }: { row: CategoryLimitRow; usage: string; reload: () => Promise<void> }) {
  const notify = useNotify()
  const [weekday, setWeekday] = useState(minutesText(row.weekdayMinutes))
  const [weekend, setWeekend] = useState(minutesText(row.weekendMinutes))

  const name = t.categoryName(row.category)
  async function save() {
    try {
      await api.setCategoryLimit(row.category, minutesOrNull(weekday), minutesOrNull(weekend))
      await reload()
    } catch (e) {
      notify.error(t.notSaved(errorMessage(e)))
    }
  }

  return (
    <tr>
      <td>
        <strong>{name}</strong>
      </td>
      <td>
        <input
          className="input input-sm"
          type="number"
          min={0}
          max={1440}
          aria-label={`${name} ${t.colWeekday}`}
          placeholder={t.noLimit}
          disabled={!row.editable}
          title={row.editable ? undefined : t.systemNeverLimited}
          value={weekday}
          onChange={(e) => setWeekday(e.target.value)}
        />
      </td>
      <td>
        <input
          className="input input-sm"
          type="number"
          min={0}
          max={1440}
          aria-label={`${name} ${t.colWeekend}`}
          placeholder={t.noLimit}
          disabled={!row.editable}
          title={row.editable ? undefined : t.systemNeverLimited}
          value={weekend}
          onChange={(e) => setWeekend(e.target.value)}
        />
      </td>
      <td>{usage}</td>
      <td>
        {row.editable && (
          <button type="button" className="btn btn-secondary" aria-label={`${name} ${t.save}`} onClick={() => void save()}>
            💾 {t.save}
          </button>
        )}
      </td>
    </tr>
  )
}

function RuleRow({ rule, reload, reloadDashboard }: { rule: AppRule; reload: () => Promise<void>; reloadDashboard: () => void }) {
  const notify = useNotify()
  const [category, setCategory] = useState<Category>(rule.category)
  const [weekday, setWeekday] = useState(minutesText(rule.weekdayMinutes))
  const [weekend, setWeekend] = useState(minutesText(rule.weekendMinutes))

  async function save() {
    try {
      await api.updateAppRule(rule.id, {
        matchType: rule.matchType,
        pattern: rule.pattern,
        category,
        weekdayMinutes: minutesOrNull(weekday),
        weekendMinutes: minutesOrNull(weekend),
      })
      await reload()
    } catch (e) {
      notify.error(t.notSaved(errorMessage(e)))
    }
  }

  async function remove() {
    if (!window.confirm(t.confirmDeleteRule(rule.pattern))) return
    try {
      await api.deleteAppRule(rule.id)
      await reload()
      reloadDashboard()
    } catch (e) {
      notify.error(t.notDeleted(errorMessage(e)))
    }
  }

  return (
    <tr>
      <td>
        {rule.matchType === 'file' ? t.matchFile : t.matchFolder} {rule.isDefault && <small>{t.defaultRule}</small>}
      </td>
      <td>
        <code>{rule.pattern}</code>
      </td>
      <td>
        <CategorySelect value={category} onChange={(c) => c && setCategory(c)} label={`${t.colCategory}: ${rule.pattern}`} />
      </td>
      <td>
        <input
          className="input input-sm"
          type="number"
          min={0}
          max={1440}
          aria-label={`${t.colAppLimitWeekday}: ${rule.pattern}`}
          placeholder={t.categoryPlaceholder}
          value={weekday}
          onChange={(e) => setWeekday(e.target.value)}
        />
      </td>
      <td>
        <input
          className="input input-sm"
          type="number"
          min={0}
          max={1440}
          aria-label={`${t.colAppLimitWeekend}: ${rule.pattern}`}
          placeholder={t.categoryPlaceholder}
          value={weekend}
          onChange={(e) => setWeekend(e.target.value)}
        />
      </td>
      <td className="row">
        <button type="button" className="btn btn-secondary" aria-label={t.saveRule(rule.pattern)} onClick={() => void save()}>
          💾
        </button>
        <button type="button" className="btn btn-secondary" aria-label={t.deleteRule(rule.pattern)} onClick={() => void remove()}>
          🗑️
        </button>
      </td>
    </tr>
  )
}

function NewRuleForm({ reload, reloadDashboard }: { reload: () => Promise<void>; reloadDashboard: () => void }) {
  const notify = useNotify()
  const [matchType, setMatchType] = useState<RuleMatchType>('file')
  const [pattern, setPattern] = useState('')
  const [category, setCategory] = useState<Category>('Games')
  const [weekday, setWeekday] = useState('')
  const [weekend, setWeekend] = useState('')

  async function add() {
    try {
      await api.addAppRule({
        matchType,
        pattern,
        category,
        weekdayMinutes: minutesOrNull(weekday),
        weekendMinutes: minutesOrNull(weekend),
      })
      setPattern('')
      setWeekday('')
      setWeekend('')
      await reload()
      reloadDashboard()
    } catch (e) {
      notify.error(t.notAdded(errorMessage(e)))
    }
  }

  return (
    <div className="row">
      <select className="input input-sm" aria-label={t.colType} value={matchType} onChange={(e) => setMatchType(e.target.value as RuleMatchType)}>
        <option value="file">{t.matchFile}</option>
        <option value="folder">{t.matchFolder}</option>
      </select>
      <input className="input input-sm input-wide" aria-label={t.colPattern} placeholder={t.patternPlaceholder} value={pattern} onChange={(e) => setPattern(e.target.value)} />
      <CategorySelect value={category} onChange={(c) => c && setCategory(c)} label={t.colCategory} />
      <input className="input input-sm" type="number" min={0} max={1440} aria-label={t.appLimitWeekdayPlaceholder} placeholder={t.appLimitWeekdayPlaceholder} value={weekday} onChange={(e) => setWeekday(e.target.value)} />
      <input className="input input-sm" type="number" min={0} max={1440} aria-label={t.appLimitWeekendPlaceholder} placeholder={t.appLimitWeekendPlaceholder} value={weekend} onChange={(e) => setWeekend(e.target.value)} />
      <button type="button" className="btn btn-primary" onClick={() => void add()}>
        ➕ {t.addRule}
      </button>
    </div>
  )
}

/** Category Limits and App Rules with App Limits. */
export function CategoriesPanel({ categories, rules, loadError, reload, reloadDashboard }: Props) {
  return (
    <section className="card section" aria-labelledby="categories-title">
      <h2 id="categories-title">🗂️ {t.categoriesTitle}</h2>
      <p className="hint">{t.categoriesHint}</p>
      {loadError && <div className="banner banner-error">{t.categoriesLoadFailed(loadError)}</div>}
      {categories === null ? (
        !loadError && <div className="muted">{t.loading}</div>
      ) : (
        <div className="table-responsive">
          <table>
            <thead>
              <tr>
                <th>{t.colCategory}</th>
                <th>{t.colWeekday}</th>
                <th>{t.colWeekend}</th>
                <th>{t.colUsedToday}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {categories.categories.map((row) => {
                const usage =
                  categories.usageToday
                    .filter((u) => u.category === row.category)
                    .map((u) => `${u.username}: ${formatMinutes(u.minutes)}`)
                    .join(', ') || '-'
                // The key holds the saved values: a reload with new values starts the row again with them.
                return <LimitRow key={`${row.category}:${row.weekdayMinutes}:${row.weekendMinutes}`} row={row} usage={usage} reload={reload} />
              })}
            </tbody>
          </table>
        </div>
      )}

      <h3>📏 {t.rulesTitle}</h3>
      <p className="hint hint-strong">{t.rulesHint}</p>
      <p className="hint hint-strong">{t.appLimitHint}</p>
      {rules !== null &&
        (rules.length === 0 ? (
          <div className="empty">{t.rulesEmpty}</div>
        ) : (
          <div className="table-responsive">
            <table>
              <thead>
                <tr>
                  <th>{t.colType}</th>
                  <th>{t.colPattern}</th>
                  <th>{t.colCategory}</th>
                  <th>{t.colAppLimitWeekday}</th>
                  <th>{t.colAppLimitWeekend}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {rules.map((rule) => (
                  <RuleRow key={`${rule.id}:${rule.category}:${rule.weekdayMinutes}:${rule.weekendMinutes}`} rule={rule} reload={reload} reloadDashboard={reloadDashboard} />
                ))}
              </tbody>
            </table>
          </div>
        ))}
      <NewRuleForm reload={reload} reloadDashboard={reloadDashboard} />
    </section>
  )
}
