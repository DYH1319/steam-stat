/** 时长格式：133.9h / 133h 55m / 5天 13小时 */
export type DurationFormat = 'hours' | 'hm' | 'dh'

export interface DurationUnits {
  day: string
  hour: string
  minute: string
}

const DEFAULT_UNITS: DurationUnits = { day: 'd', hour: 'h', minute: 'm' }

export function formatDuration(seconds: number, format: DurationFormat = 'hm', units: DurationUnits = DEFAULT_UNITS): string {
  const total = Math.max(0, Math.round(seconds))
  if (format === 'hours') {
    const hours = total / 3600
    return `${hours >= 10 ? hours.toFixed(1) : hours.toFixed(2)}${units.hour}`
  }
  const minutes = Math.floor(total / 60) % 60
  const hours = Math.floor(total / 3600)
  if (format === 'dh' && hours >= 24) {
    const days = Math.floor(hours / 24)
    return `${days}${units.day} ${hours % 24}${units.hour}`
  }
  if (hours === 0) {
    return `${minutes}${units.minute}`
  }
  return `${hours}${units.hour} ${minutes}${units.minute}`
}

/** 变化率格式（+12.5% / -3.0%） */
export function formatRatio(ratio: number | null): string {
  if (ratio == null) {
    return '—'
  }
  const percent = ratio * 100
  return `${percent >= 0 ? '+' : ''}${percent.toFixed(1)}%`
}

/** 分钟数 → HH:mm（≥ 24h 表示次日凌晨） */
export function formatClockMinutes(minutes: number): string {
  const normalized = minutes % (24 * 60)
  return `${String(Math.floor(normalized / 60)).padStart(2, '0')}:${String(normalized % 60).padStart(2, '0')}`
}
