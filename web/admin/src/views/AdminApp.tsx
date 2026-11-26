import { useCallback, useEffect, useMemo, useState } from 'react'
import { ADMIN_PAGE, api } from '../api/client'
import type { AppRule, Categories, Dashboard, DashboardQuery, GraceRequest, PromptSend, UpdateCheck } from '../api/types'
import { NoticeProvider, errorMessage, useNotify } from '../components/notices'
import { LockDialog } from '../dialogs/LockDialog'
import { PromptDialog } from '../dialogs/PromptDialog'
import { ScheduleDialog } from '../dialogs/ScheduleDialog'
import { SettingsDialog } from '../dialogs/SettingsDialog'
import { UpdateDialog } from '../dialogs/UpdateDialog'
import { t } from '../i18n'
import { ActivityPanel, presetQuery, type DatePreset } from './ActivityPanel'
import { CapturePanel } from './CapturePanel'
import { CategoriesPanel } from './CategoriesPanel'
import { RequestsCard } from './RequestsCard'
import { SessionsCard, type SessionActions } from './SessionsCard'
import { UsersCard } from './UsersCard'
import { useCaptures } from './useCaptures'

/** The dashboard reloads every 10 seconds, as on the old page. */
export const DASHBOARD_REFRESH_MS = 10_000

type Dialog =
  | { kind: 'lock'; sessionId: number; username: string }
  | { kind: 'prompt'; username: string }
  | { kind: 'schedule'; userId: number; username: string }
  | { kind: 'update' }
  | { kind: 'settings' }
  | null

export function AdminApp({ version }: { version: string }) {
  return (
    <NoticeProvider>
      <AdminPage version={version} />
    </NoticeProvider>
  )
}

function AdminPage({ version }: { version: string }) {
  const notify = useNotify()
  const [query, setQuery] = useState<DashboardQuery>(() => ({ user: 'all', ...presetQuery('today') }))
  const [preset, setPreset] = useState<DatePreset>('today')
  const [data, setData] = useState<Dashboard | null>(null)
  const [tick, setTick] = useState(0)
  const [categories, setCategories] = useState<Categories | null>(null)
  const [rules, setRules] = useState<AppRule[] | null>(null)
  const [categoriesError, setCategoriesError] = useState<string | null>(null)
  const [dialog, setDialog] = useState<Dialog>(null)
  const [update, setUpdate] = useState<UpdateCheck | null>(null)

  const loadDashboard = useCallback(async () => {
    try {
      setData(await api.getDashboard(query))
      setTick((n) => n + 1)
    } catch {
      // The next refresh tries again. A 401 reloads the page (client.ts).
    }
  }, [query])

  useEffect(() => {
    void loadDashboard()
    const id = window.setInterval(() => void loadDashboard(), DASHBOARD_REFRESH_MS)
    return () => window.clearInterval(id)
  }, [loadDashboard])

  const loadCategories = useCallback(async () => {
    try {
      const [cats, ruleList] = await Promise.all([api.getCategories(), api.getAppRules()])
      setCategories(cats)
      setRules(ruleList.rules)
      setCategoriesError(null)
    } catch (e) {
      setCategoriesError(errorMessage(e))
    }
  }, [])

  useEffect(() => {
    void loadCategories()
  }, [loadCategories])

  const activeChildren = useMemo(
    () => (data?.sessions ?? []).filter((s) => s.isRestricted && !s.isLocked).map((s) => s.username),
    [data],
  )
  const captures = useCaptures(query.user, activeChildren, tick)

  const run = useCallback(
    async (action: () => Promise<unknown>, success?: string) => {
      try {
        await action()
        if (success) notify.ok(success)
      } catch (e) {
        notify.error(errorMessage(e))
      }
      void loadDashboard()
    },
    [notify, loadDashboard],
  )

  const sessionActions: SessionActions = {
    prompt: (username) => setDialog({ kind: 'prompt', username }),
    capture: (username) => void captures.captureNow(username),
    schedule: (userId, username) => setDialog({ kind: 'schedule', userId, username }),
    lock: (sessionId, username) => setDialog({ kind: 'lock', sessionId, username }),
    grant: (userId, username, minutes) => {
      if (window.confirm(t.confirmGrant(minutes, username))) void run(() => api.grantMinutes(userId, minutes))
    },
    logoff: (sessionId) => {
      if (window.confirm(t.confirmLogoff(sessionId))) void run(() => api.forceLogoff(sessionId))
    },
  }

  function resolve(request: GraceRequest, action: 'approve' | 'decline') {
    void run(() => api.resolveRequest(request.id, action, action === 'approve' ? request.requestedMinutes : 0))
  }

  function sendPrompt(prompt: PromptSend) {
    setDialog(null)
    void (async () => {
      try {
        await api.sendPrompt(prompt)
        notify.ok(t.promptSent(prompt.username))
        void loadDashboard()
      } catch (e) {
        notify.error(t.promptFailed(errorMessage(e)))
      }
    })()
  }

  function lock(sessionId: number, seconds: number) {
    setDialog(null)
    void run(async () => {
      const res = await api.lockSession(sessionId, seconds)
      if (res.message) notify.ok(res.message)
    })
  }

  async function logout() {
    try {
      await api.logout()
    } finally {
      window.location.assign(ADMIN_PAGE)
    }
  }

  function choosePreset(next: Exclude<DatePreset, 'custom'>) {
    setPreset(next)
    setQuery((q) => ({ ...q, ...presetQuery(next) }))
  }

  const closeDialog = useCallback(() => setDialog(null), [])

  return (
    <div className="page">
      <header className="top">
        <div className="brand">
          <h1>🛡️ {t.headerTitle}</h1>
          {version && <span className="badge badge-neutral">{version}</span>}
          {update?.hasUpdate && (
            <button type="button" className="badge badge-approved" onClick={() => setDialog({ kind: 'update' })}>
              🚀 {t.updateAvailableBadge(`v${update.latestVersion ?? ''}`)}
            </button>
          )}
        </div>
        <nav className="actions">
          <button type="button" className="btn btn-secondary" onClick={() => setDialog({ kind: 'settings' })}>
            ⚙️ {t.settings}
          </button>
          <button type="button" className="btn btn-secondary" onClick={() => setDialog({ kind: 'update' })}>
            🚀 {t.checkUpdate}
          </button>
          <button type="button" className="btn btn-secondary" onClick={() => void loadDashboard()}>
            🔄 {t.refresh}
          </button>
          <button type="button" className="btn btn-secondary" onClick={() => void logout()}>
            🚪 {t.logout}
          </button>
        </nav>
      </header>

      <main>
        <div className="grid">
          <SessionsCard sessions={data?.sessions ?? null} actions={sessionActions} />
          <RequestsCard requests={data?.requests ?? null} onResolve={resolve} />
        </div>
        <UsersCard users={data?.managedUsers ?? null} onManage={(userId, username) => setDialog({ kind: 'schedule', userId, username })} />
        <CapturePanel state={captures} />
        <CategoriesPanel
          categories={categories}
          rules={rules}
          loadError={categoriesError}
          reload={loadCategories}
          reloadDashboard={() => void loadDashboard()}
        />
        <ActivityPanel
          data={data}
          query={query}
          preset={preset}
          onUser={(user) => setQuery((q) => ({ ...q, user }))}
          onPreset={choosePreset}
          onDate={(date) => {
            setPreset('custom')
            setQuery((q) => ({ ...q, date, range: 'day' }))
          }}
          rules={rules}
          reloadCategories={loadCategories}
          reloadDashboard={() => void loadDashboard()}
        />
      </main>

      {dialog?.kind === 'lock' && (
        <LockDialog username={dialog.username} onClose={closeDialog} onConfirm={(seconds) => lock(dialog.sessionId, seconds)} />
      )}
      {dialog?.kind === 'prompt' && <PromptDialog username={dialog.username} onClose={closeDialog} onSend={sendPrompt} />}
      {dialog?.kind === 'schedule' && (
        <ScheduleDialog userId={dialog.userId} username={dialog.username} onClose={closeDialog} onSaved={() => void loadDashboard()} />
      )}
      {dialog?.kind === 'update' && <UpdateDialog onClose={closeDialog} onChecked={setUpdate} />}
      {dialog?.kind === 'settings' && <SettingsDialog onClose={closeDialog} />}
    </div>
  )
}
