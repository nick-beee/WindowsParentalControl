/** "1h 5m" style, in German: "1 Std. 5 Min.", "45 Min.", "0 Min.". */
export function formatMinutes(minutes: number): string {
  if (!Number.isFinite(minutes) || minutes <= 0) return '0 Min.'
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  return h > 0 ? `${h} Std. ${m} Min.` : `${m} Min.`
}

/** The local date as yyyy-MM-dd. */
export function isoDate(date: Date): string {
  const y = date.getFullYear()
  const m = String(date.getMonth() + 1).padStart(2, '0')
  const d = String(date.getDate()).padStart(2, '0')
  return `${y}-${m}-${d}`
}

export function addDays(date: Date, days: number): Date {
  const copy = new Date(date)
  copy.setDate(copy.getDate() + days)
  return copy
}

/** The value of a minute field: empty = null (no limit), else a number. */
export function minutesOrNull(text: string): number | null {
  const trimmed = text.trim()
  return trimmed === '' ? null : Number(trimmed)
}

export function minutesText(value: number | null | undefined): string {
  return value === null || value === undefined ? '' : String(value)
}

/** The file name of a file-name rule for a process name of the App Usage table. */
export function exeFileName(processName: string): string {
  return /\.exe$/i.test(processName) ? processName : `${processName}.exe`
}

/**
 * The local time of an ISO 8601 time as HH:mm (the time of the PC that shows the page). The service sends 7 digits
 * after the seconds (.NET "O"); some browsers do not parse more than 3, so the function cuts the rest.
 */
export function clockTime(iso: string): string {
  const date = new Date(iso.replace(/(\.\d{3})\d+/, '$1'))
  if (Number.isNaN(date.getTime())) return iso
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
}
