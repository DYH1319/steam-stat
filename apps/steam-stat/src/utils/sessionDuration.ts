/**
 * 会话时长格式化：>= 1h 显示 "Xh Ym"，否则 "Ym Zs"；null/负数返回 "—"。
 */
export function formatSessionDuration(seconds: number | null | undefined): string {
  if (seconds == null || seconds < 0) {
    return '—'
  }
  const total = Math.floor(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const secs = total % 60
  if (hours > 0) {
    return `${hours}h ${minutes}m`
  }
  return `${minutes}m ${secs}s`
}
