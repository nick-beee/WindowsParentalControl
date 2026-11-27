import { useEffect, useState } from 'react'
import { formatCountdown } from './rules'

/**
 * The remaining time of today. It goes down by one second each second while the session runs and is not locked,
 * and it shows the time in the title of the tab, as the old page did.
 */
export function Countdown({ seconds, ticking, overText }: { seconds: number; ticking: boolean; overText: string }) {
  const [left, setLeft] = useState(seconds)

  useEffect(() => {
    if (!ticking) return
    const timer = window.setInterval(() => setLeft((s) => (s > 0 ? s - 1 : s)), 1000)
    return () => window.clearInterval(timer)
  }, [ticking])

  const text = formatCountdown(left, overText)
  useEffect(() => {
    document.title = `[${text}] Parental Control`
  }, [text])

  return (
    <div className={left <= 0 ? 'timer-value timer-over' : 'timer-value'} role="timer">
      {text}
    </div>
  )
}
