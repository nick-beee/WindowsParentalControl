import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { Capture } from '../api/types'
import { errorMessage, useNotify } from '../components/notices'
import { t } from '../i18n'

/** The capture reloads after a capture request, as on the old page (milliseconds). */
export const CAPTURE_RELOAD_DELAYS = [2000, 4500, 7000]
export const WATCH_RELOAD_MS = 10_000
export const WATCH_HEARTBEAT_MS = 60_000

/**
 * The screen capture state. `filterUser` is the user filter of the page ("all" or a name),
 * `activeChildren` the restricted users with an unlocked session, and `refreshTick` changes with each
 * dashboard load (the old page reloaded the capture with the dashboard).
 */
export function useCaptures(filterUser: string, activeChildren: string[], refreshTick: number) {
  const notify = useNotify()
  const latestId = useRef<number | null>(null)
  const [recent, setRecent] = useState<Capture[]>([])
  const [selected, setSelected] = useState<Capture | null>(null)
  const [capturing, setCapturing] = useState(false)
  const [sending, setSending] = useState(false)
  const [watchUser, setWatchUser] = useState<string | null>(null)
  const timers = useRef<number[]>([])

  const target = filterUser !== 'all' ? filterUser : (activeChildren[0] ?? '')
  const loadUser = target || 'all'

  const load = useCallback(async () => {
    try {
      const [{ capture }, { captures }] = await Promise.all([
        api.getLatestCapture(loadUser),
        api.getRecentCaptures(loadUser, 10),
      ])
      setRecent(captures ?? [])
      // A new capture replaces the selection; a selected older capture stays until then.
      if (capture && latestId.current !== capture.id) {
        latestId.current = capture.id
        setSelected(capture)
      }
    } catch {
      // The next dashboard load tries again; a 401 reloads the page in the client.
    }
  }, [loadUser])

  useEffect(() => {
    void load()
  }, [load, refreshTick])

  // Timers of single capture requests.
  useEffect(() => {
    const list = timers.current
    return () => list.forEach((id) => window.clearTimeout(id))
  }, [])

  // Watch mode: reload every 10 s, and send a heartbeat every 60 s so the service keeps it on.
  useEffect(() => {
    if (!watchUser) return
    const reload = window.setInterval(() => void load(), WATCH_RELOAD_MS)
    const heartbeat = window.setInterval(() => void api.watchHeartbeat(watchUser).catch(() => {}), WATCH_HEARTBEAT_MS)
    return () => {
      window.clearInterval(reload)
      window.clearInterval(heartbeat)
    }
  }, [watchUser, load])

  const captureNow = useCallback(
    async (username?: string) => {
      const who = username ?? target
      if (!who) {
        notify.error(t.noActiveChild)
        return
      }
      setCapturing(true)
      try {
        await api.requestCapture(who)
        for (const delay of CAPTURE_RELOAD_DELAYS) timers.current.push(window.setTimeout(() => void load(), delay))
      } catch (e) {
        notify.error(`${t.captureFailed} ${errorMessage(e)}`)
      } finally {
        setCapturing(false)
      }
    },
    [target, load, notify],
  )

  const toggleWatch = useCallback(async () => {
    if (watchUser) {
      const who = watchUser
      setWatchUser(null)
      await api.setWatchMode(who, false).catch((e: unknown) => notify.error(errorMessage(e)))
      return
    }
    if (!target) {
      notify.error(t.noActiveChild)
      return
    }
    try {
      await api.setWatchMode(target, true)
      setWatchUser(target)
      void load()
    } catch (e) {
      notify.error(errorMessage(e))
    }
  }, [watchUser, target, load, notify])

  const sendToTelegram = useCallback(async () => {
    if (!selected) {
      notify.error(t.noCaptureLoaded)
      return
    }
    setSending(true)
    try {
      const res = await api.sendCaptureToTelegram(selected.id)
      if (res.success) notify.ok(t.telegramSent)
      else notify.error(t.telegramFailed)
    } catch (e) {
      notify.error(errorMessage(e))
    } finally {
      setSending(false)
    }
  }, [selected, notify])

  return {
    recent,
    selected,
    select: setSelected,
    capturing,
    sending,
    watching: watchUser !== null,
    captureNow,
    toggleWatch,
    sendToTelegram,
  }
}

export type CaptureState = ReturnType<typeof useCaptures>
