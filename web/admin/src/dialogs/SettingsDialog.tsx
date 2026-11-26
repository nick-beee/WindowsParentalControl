import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Settings } from '../api/types'
import { Modal } from '../components/Modal'
import { errorMessage, useNotify } from '../components/notices'
import { t } from '../i18n'

/** Telegram notifications, prompt answer presets and the texts of the category messages. */
export function SettingsDialog({ onClose }: { onClose: () => void }) {
  const notify = useNotify()
  const [settings, setSettings] = useState<Settings | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let active = true
    api
      .getSettings()
      .then((s) => {
        if (!active) return
        setSettings({
          ...s,
          promptResponsePresets: s.promptResponsePresets ?? '',
          msgCategoryWarn: s.msgCategoryWarn ?? '',
          msgCategorySuspended: s.msgCategorySuspended ?? '',
        })
      })
      .catch((e: unknown) => active && setLoadError(errorMessage(e)))
    return () => {
      active = false
    }
  }, [])

  function update<K extends keyof Settings>(key: K, value: Settings[K]) {
    setSettings((s) => (s ? { ...s, [key]: value } : s))
  }

  async function save() {
    if (!settings) return
    setSaving(true)
    try {
      await api.saveSettings({
        telegramNotifySignIn: settings.telegramNotifySignIn,
        telegramNotifySignOut: settings.telegramNotifySignOut,
        telegramNotifyAdminLogon: settings.telegramNotifyAdminLogon,
        promptResponsePresets: settings.promptResponsePresets,
        msgCategoryWarn: settings.msgCategoryWarn,
        msgCategorySuspended: settings.msgCategorySuspended,
      })
      notify.ok(t.settingsSaved)
      onClose()
    } catch (e) {
      notify.error(`${t.settingsSaveFailed} ${errorMessage(e)}`)
    } finally {
      setSaving(false)
    }
  }

  return (
    <Modal
      title={`⚙️ ${t.settingsTitle}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn btn-secondary" onClick={onClose}>
            {t.cancel}
          </button>
          <button type="button" className="btn btn-primary" disabled={!settings || saving} onClick={() => void save()}>
            💾 {saving ? t.saving : t.save}
          </button>
        </>
      }
    >
      <p className="modal-text">{t.settingsText}</p>
      {loadError && <div className="banner banner-error">{`${t.settingsLoadFailed} ${loadError}`}</div>}
      {!settings && !loadError && <p className="muted">{t.loading}</p>}
      {settings && (
        <div className="stack">
          {!settings.telegramConfigured && <div className="banner banner-warn">⚠️ {t.telegramMissing}</div>}
          <label className="check">
            <input type="checkbox" checked={settings.telegramNotifySignIn} onChange={(e) => update('telegramNotifySignIn', e.target.checked)} />
            <span>{t.notifySignIn}</span>
          </label>
          <label className="check">
            <input type="checkbox" checked={settings.telegramNotifySignOut} onChange={(e) => update('telegramNotifySignOut', e.target.checked)} />
            <span>{t.notifySignOut}</span>
          </label>
          <label className="check">
            <input type="checkbox" checked={settings.telegramNotifyAdminLogon} onChange={(e) => update('telegramNotifyAdminLogon', e.target.checked)} />
            <span>{t.notifyAdminLogon}</span>
          </label>
          <div>
            <label className="field" htmlFor="cfg-presets">
              {t.promptPresetsLabel}
            </label>
            <textarea id="cfg-presets" className="full" rows={4} value={settings.promptResponsePresets} onChange={(e) => update('promptResponsePresets', e.target.value)} />
          </div>
          <div>
            <label className="field" htmlFor="cfg-warn">
              {t.msgCategoryWarnLabel}
            </label>
            <textarea id="cfg-warn" className="full" rows={2} value={settings.msgCategoryWarn} onChange={(e) => update('msgCategoryWarn', e.target.value)} />
          </div>
          <div>
            <label className="field" htmlFor="cfg-suspended">
              {t.msgCategorySuspendedLabel}
            </label>
            <textarea id="cfg-suspended" className="full" rows={2} value={settings.msgCategorySuspended} onChange={(e) => update('msgCategorySuspended', e.target.value)} />
          </div>
        </div>
      )}
    </Modal>
  )
}
