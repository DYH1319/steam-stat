import dayjs from '@/utils/dayjs'

/** 统计偏好：逻辑日的起点（0-6 点）与一周起始日（0=周日, 1=周一） */
export interface StatsPrefs {
  dayStartHour: number
  weekStartsOn: 0 | 1
}

/** 输入会话行（IPC UseAppRecord 的子集） */
export interface SessionInput {
  appId: number
  steamId?: string
  startTime: number
  endTime: number | null
  duration: number | null
  idleDuration: number
  appName?: string | null
  appNameLocalized?: string | null
  userPersonaName?: string | null
}

export interface SessionPiece {
  session: SessionInput
  appId: number
  startTime: number
  endTime: number
  duration: number
  idleDuration: number
  isStartPiece: boolean
}

export const DEFAULT_PREFS: StatsPrefs = { dayStartHour: 0, weekStartsOn: 1 }

/** 逻辑日 key（YYYY-MM-DD）：dayStartHour 之前的时间属于前一日 */
export function logicalDayKey(unix: number, prefs: StatsPrefs): string {
  return dayjs.unix(unix).subtract(prefs.dayStartHour, 'hour').format('YYYY-MM-DD')
}

/** 逻辑日起点 unix */
export function logicalDayStart(key: string, prefs: StatsPrefs): number {
  return dayjs(key, 'YYYY-MM-DD').add(prefs.dayStartHour, 'hour').unix()
}

/** 下一自然日 key（用 dayjs 加法而非 +86400，兼容 25h 的 DST 回退日） */
export function nextDayKey(key: string): string {
  return dayjs(key, 'YYYY-MM-DD').add(1, 'day').format('YYYY-MM-DD')
}

/** 逻辑日结束 unix = 下一逻辑日的起点 */
function dayEnd(key: string, prefs: StatsPrefs): number {
  return logicalDayStart(nextDayKey(key), prefs)
}

/** 将 [start,end) 按 [from,to) 裁剪；duration/idle 按跨度比例缩放 */
export function clipSessions(sessions: SessionInput[], from: number, to: number): SessionPiece[] {
  const pieces: SessionPiece[] = []
  for (const s of sessions) {
    if (s.endTime == null || s.duration == null || s.duration <= 0) {
      continue
    }
    const span = s.endTime - s.startTime
    if (span <= 0) {
      continue
    }
    const start = Math.max(s.startTime, from)
    const end = Math.min(s.endTime, to)
    if (end <= start) {
      continue
    }
    const ratio = (end - start) / span
    pieces.push({
      session: s,
      appId: s.appId,
      startTime: start,
      endTime: end,
      duration: s.duration * ratio,
      idleDuration: s.idleDuration * ratio,
      isStartPiece: s.startTime >= from && s.startTime < to,
    })
  }
  return pieces
}

/** 每个逻辑日的秒数 Map */
export function splitByLogicalDay(sessions: SessionInput[], prefs: StatsPrefs): Map<string, number> {
  const map = new Map<string, number>()
  for (const s of sessions) {
    if (s.endTime == null || s.duration == null || s.duration <= 0) {
      continue
    }
    const span = s.endTime - s.startTime
    if (span <= 0) {
      continue
    }
    let cursor = s.startTime
    while (cursor < s.endTime) {
      const key = logicalDayKey(cursor, prefs)
      const pieceEnd = Math.min(s.endTime, dayEnd(key, prefs))
      map.set(key, (map.get(key) ?? 0) + (pieceEnd - cursor) * (s.duration / span))
      cursor = pieceEnd
    }
  }
  return map
}

export interface SeriesBucket {
  key: string
  label: string
  start: number
  seconds: number
}

/** 连续时间序列（零填充）：hour 用时钟小时，day/week/month 基于逻辑日 */
export function bucketSeries(
  sessions: SessionInput[],
  granularity: 'hour' | 'day' | 'week' | 'month',
  from: number,
  to: number,
  prefs: StatsPrefs,
): SeriesBucket[] {
  if (granularity === 'hour') {
    const byHour = new Map<number, number>()
    for (const s of sessions) {
      if (s.endTime == null || s.duration == null || s.duration <= 0) {
        continue
      }
      const span = s.endTime - s.startTime
      if (span <= 0) {
        continue
      }
      let cursor = Math.max(s.startTime, from)
      const end = Math.min(s.endTime, to)
      while (cursor < end) {
        const hourStart = Math.floor(cursor / 3600) * 3600
        const pieceEnd = Math.min(end, hourStart + 3600)
        byHour.set(hourStart, (byHour.get(hourStart) ?? 0) + (pieceEnd - cursor) * (s.duration / span))
        cursor = pieceEnd
      }
    }
    const buckets: SeriesBucket[] = []
    const first = Math.floor(from / 3600) * 3600
    for (let h = first; h < to; h += 3600) {
      buckets.push({ key: String(h), label: dayjs.unix(h).format('MM-DD HH:mm'), start: h, seconds: byHour.get(h) ?? 0 })
    }
    return buckets
  }

  const byDay = splitByLogicalDay(sessions, prefs)
  const buckets: SeriesBucket[] = []
  if (granularity === 'day') {
    for (let key = logicalDayKey(from, prefs); logicalDayStart(key, prefs) < to; key = logicalDayKey(dayEnd(key, prefs), prefs)) {
      buckets.push({ key, label: dayjs(key, 'YYYY-MM-DD').format('MM-DD'), start: logicalDayStart(key, prefs), seconds: byDay.get(key) ?? 0 })
    }
    return buckets
  }

  const weekOffset = (d: dayjs.Dayjs) => (d.day() - prefs.weekStartsOn + 7) % 7
  if (granularity === 'week') {
    const weeks = new Map<string, number>()
    for (const [key, seconds] of byDay) {
      const d = dayjs(key, 'YYYY-MM-DD').subtract(weekOffset(dayjs(key, 'YYYY-MM-DD')), 'day')
      const wk = d.format('YYYY-MM-DD')
      weeks.set(wk, (weeks.get(wk) ?? 0) + seconds)
    }
    let d = dayjs(logicalDayKey(from, prefs), 'YYYY-MM-DD')
    d = d.subtract(weekOffset(d), 'day')
    for (; d.unix() + prefs.dayStartHour * 3600 < to; d = d.add(7, 'day')) {
      const wk = d.format('YYYY-MM-DD')
      buckets.push({ key: wk, label: wk, start: d.unix() + prefs.dayStartHour * 3600, seconds: weeks.get(wk) ?? 0 })
    }
    return buckets
  }

  // month
  const months = new Map<string, number>()
  for (const [key, seconds] of byDay) {
    const mk = key.slice(0, 7)
    months.set(mk, (months.get(mk) ?? 0) + seconds)
  }
  let m = dayjs(logicalDayKey(from, prefs), 'YYYY-MM-DD').startOf('month')
  for (; m.unix() < to; m = m.add(1, 'month')) {
    const mk = m.format('YYYY-MM')
    buckets.push({ key: mk, label: mk, start: m.unix(), seconds: months.get(mk) ?? 0 })
  }
  return buckets
}

export interface AppTotal {
  appId: number
  seconds: number
  effectiveSeconds: number
  count: number
  lastPlayed: number
}

export function totalsByApp(pieces: SessionPiece[]): AppTotal[] {
  const map = new Map<number, AppTotal>()
  for (const p of pieces) {
    const t = map.get(p.appId) ?? { appId: p.appId, seconds: 0, effectiveSeconds: 0, count: 0, lastPlayed: 0 }
    t.seconds += p.duration
    t.effectiveSeconds += Math.max(0, p.duration - p.idleDuration)
    if (p.isStartPiece) {
      t.count += 1
    }
    t.lastPlayed = Math.max(t.lastPlayed, p.endTime)
    map.set(p.appId, t)
  }
  return [...map.values()].sort((a, b) => b.seconds - a.seconds)
}

export function topN<T extends { seconds: number }>(list: T[], n: number, withOthers: boolean): T[] {
  if (list.length <= n) {
    return list
  }
  const top = list.slice(0, n)
  if (withOthers) {
    top.push({ seconds: list.slice(n).reduce((s, x) => s + x.seconds, 0) } as T)
  }
  return top
}

/** 7×24 时钟小时×星期 秒数矩阵，行序随 weekStartsOn */
export function hourWeekMatrix(sessions: SessionInput[], weekStartsOn: 0 | 1): number[][] {
  const matrix: number[][] = Array.from({ length: 7 }, () => Array.from<number>({ length: 24 }).fill(0))
  for (const s of sessions) {
    if (s.endTime == null || s.duration == null || s.duration <= 0) {
      continue
    }
    const span = s.endTime - s.startTime
    if (span <= 0) {
      continue
    }
    let cursor = s.startTime
    while (cursor < s.endTime) {
      const hourStart = Math.floor(cursor / 3600) * 3600
      const pieceEnd = Math.min(s.endTime, hourStart + 3600)
      const d = dayjs.unix(cursor)
      const row = (d.day() - weekStartsOn + 7) % 7
      matrix[row][d.hour()] += (pieceEnd - cursor) * (s.duration / span)
      cursor = pieceEnd
    }
  }
  return matrix
}

/** 某自然年每日秒数 */
export function yearCalendar(sessions: SessionInput[], year: number, prefs: StatsPrefs): [string, number][] {
  const byDay = splitByLogicalDay(sessions, prefs)
  const result: [string, number][] = []
  for (const [key, seconds] of byDay) {
    if (key.startsWith(String(year))) {
      result.push([key, seconds])
    }
  }
  return result.sort((a, b) => a[0].localeCompare(b[0]))
}

export interface GanttSegment {
  appId: number
  startOffset: number
  endOffset: number
  realStart: number
  realEnd: number
}

export interface GanttDay {
  day: string
  segments: GanttSegment[]
}

/** 按逻辑日拆分的甘特段（startOffset/endOffset 为距逻辑日起点的秒数） */
export function ganttRows(sessions: SessionInput[], fromDayKey: string, toDayKey: string, prefs: StatsPrefs): GanttDay[] {
  const rows = new Map<string, GanttSegment[]>()
  for (const s of sessions) {
    if (s.endTime == null || s.duration == null || s.duration <= 0) {
      continue
    }
    const span = s.endTime - s.startTime
    if (span <= 0) {
      continue
    }
    let cursor = s.startTime
    while (cursor < s.endTime) {
      const key = logicalDayKey(cursor, prefs)
      const dayStart = logicalDayStart(key, prefs)
      const pieceEnd = Math.min(s.endTime, dayEnd(key, prefs))
      if (key >= fromDayKey && key <= toDayKey) {
        const list = rows.get(key) ?? []
        list.push({
          appId: s.appId,
          startOffset: Math.max(0, cursor - dayStart),
          endOffset: Math.min(86400, pieceEnd - dayStart),
          realStart: cursor,
          realEnd: pieceEnd,
        })
        rows.set(key, list)
      }
      cursor = pieceEnd
    }
  }
  const days: GanttDay[] = []
  for (let key = fromDayKey; key <= toDayKey; key = logicalDayKey(dayEnd(key, prefs), prefs)) {
    days.push({ day: key, segments: (rows.get(key) ?? []).sort((a, b) => a.startOffset - b.startOffset) })
  }
  return days
}

export interface LifecyclePoint {
  time: number
  cumulative: number
}

/** 每 app 的累计时长曲线；首个点为首会话开始时刻、值为 baseline */
export function lifecycle(
  sessions: SessionInput[],
  appIds: number[],
  baselineSecondsByApp?: Map<number, number>,
): Map<number, LifecyclePoint[]> {
  const result = new Map<number, LifecyclePoint[]>()
  for (const appId of appIds) {
    const list = sessions
      .filter(s => s.appId === appId && s.endTime != null && s.duration != null && s.duration > 0)
      .sort((a, b) => a.endTime! - b.endTime!)
    if (list.length === 0) {
      continue
    }
    let cumulative = baselineSecondsByApp?.get(appId) ?? 0
    const points: LifecyclePoint[] = [{ time: list[0].startTime, cumulative }]
    for (const s of list) {
      cumulative += s.duration!
      points.push({ time: s.endTime!, cumulative })
    }
    result.set(appId, points)
  }
  return result
}

export interface PersonalRecords {
  longestStreak: { days: number, from: string, to: string } | null
  longestBreak: { days: number, from: string, to: string } | null
  longestSession: SessionInput | null
  latestNight: { session: SessionInput, clock: string } | null
  mostPlayedDay: { day: string, seconds: number } | null
  mostSessionsDay: { day: string, count: number } | null
  mostGamesDay: { day: string, count: number } | null
}

export function personalRecords(sessions: SessionInput[], prefs: StatsPrefs, nowUnix: number): PersonalRecords {
  const byDay = splitByLogicalDay(sessions, prefs)
  const days = [...byDay.keys()].sort()
  const result: PersonalRecords = {
    longestStreak: null,
    longestBreak: null,
    longestSession: null,
    latestNight: null,
    mostPlayedDay: null,
    mostSessionsDay: null,
    mostGamesDay: null,
  }

  // 连续游玩 / 最长中断（含到今天为止的尾段）
  let streak = 0
  let streakFrom = ''
  let gapStart: string | null = null
  const played = new Set(days)
  const todayKey = logicalDayKey(nowUnix, prefs)
  const firstKey = days[0] ?? todayKey
  const dayBefore = (k: string) => dayjs(k, 'YYYY-MM-DD').subtract(1, 'day').format('YYYY-MM-DD')
  for (let key = firstKey; key <= todayKey; key = nextDayKey(key)) {
    if (played.has(key)) {
      if (streak === 0) {
        streakFrom = key
      }
      streak++
      if (gapStart != null) {
        const gapDays = Math.round((logicalDayStart(key, prefs) - logicalDayStart(gapStart, prefs)) / 86400)
        if (gapDays > (result.longestBreak?.days ?? -1)) {
          result.longestBreak = { days: gapDays, from: gapStart, to: dayBefore(key) }
        }
        gapStart = null
      }
      if (streak > (result.longestStreak?.days ?? 0)) {
        result.longestStreak = { days: streak, from: streakFrom, to: key }
      }
    }
    else {
      if (gapStart == null && streak > 0) {
        gapStart = key
      }
      streak = 0
    }
  }
  if (gapStart != null) {
    const gapDays = Math.round((logicalDayStart(nextDayKey(todayKey), prefs) - logicalDayStart(gapStart, prefs)) / 86400)
    if (gapDays > (result.longestBreak?.days ?? 0)) {
      result.longestBreak = { days: gapDays, from: gapStart, to: todayKey }
    }
  }

  const sessionsCount = new Map<string, number>()
  const gamesCount = new Map<string, Set<number>>()
  for (const s of sessions) {
    if (s.endTime == null || s.duration == null || s.duration <= 0) {
      continue
    }
    if (s.duration > (result.longestSession?.duration ?? -1)) {
      result.longestSession = s
    }
    // 熬夜深度：会话结束距起始逻辑日起点的跨度最大者
    const night = s.endTime - logicalDayStart(logicalDayKey(s.startTime, prefs), prefs)
    const bestNight = result.latestNight
      ? result.latestNight.session.endTime! - logicalDayStart(logicalDayKey(result.latestNight.session.startTime, prefs), prefs)
      : -1
    if (night > bestNight) {
      result.latestNight = { session: s, clock: dayjs.unix(s.endTime).format('HH:mm') }
    }
    const startKey = logicalDayKey(s.startTime, prefs)
    sessionsCount.set(startKey, (sessionsCount.get(startKey) ?? 0) + 1)
    const set = gamesCount.get(startKey) ?? new Set<number>()
    set.add(s.appId)
    gamesCount.set(startKey, set)
  }
  for (const [key, seconds] of byDay) {
    if (seconds > (result.mostPlayedDay?.seconds ?? 0)) {
      result.mostPlayedDay = { day: key, seconds }
    }
  }
  for (const [key, count] of sessionsCount) {
    if (count > (result.mostSessionsDay?.count ?? 0)) {
      result.mostSessionsDay = { day: key, count }
    }
  }
  for (const [key, set] of gamesCount) {
    if (set.size > (result.mostGamesDay?.count ?? 0)) {
      result.mostGamesDay = { day: key, count: set.size }
    }
  }
  return result
}

export interface PeriodCompare {
  current: { seconds: number, sessions: number, games: number }
  previous: { seconds: number, sessions: number, games: number }
}

/** 本周/月至今 vs 上一周期等长区间 */
export function comparePeriods(sessions: SessionInput[], nowUnix: number, prefs: StatsPrefs): Record<'week' | 'month' | 'monthYoY', PeriodCompare> {
  const now = dayjs.unix(nowUnix)
  const stats = (from: number, to: number) => {
    const pieces = clipSessions(sessions, from, to)
    return {
      seconds: pieces.reduce((s, p) => s + p.duration, 0),
      sessions: pieces.filter(p => p.isStartPiece).length,
      games: new Set(pieces.map(p => p.appId)).size,
    }
  }
  const make = (currentStart: dayjs.Dayjs, prevStart: dayjs.Dayjs): PeriodCompare => {
    const elapsed = nowUnix - currentStart.unix()
    return {
      current: stats(currentStart.unix(), nowUnix),
      previous: stats(prevStart.unix(), prevStart.unix() + elapsed),
    }
  }
  const weekStart = now.subtract((now.day() - prefs.weekStartsOn + 7) % 7, 'day').startOf('day')
  const monthStart = now.startOf('month')
  return {
    week: make(weekStart, weekStart.subtract(7, 'day')),
    month: make(monthStart, monthStart.subtract(1, 'month')),
    monthYoY: make(monthStart, monthStart.subtract(1, 'year')),
  }
}

export interface MetaGroup {
  name: string
  seconds: number
}

export function groupByMeta(
  totals: AppTotal[],
  metaByAppId: Map<number, Record<string, string | undefined>>,
  field: 'developer' | 'publisher' | 'type',
): MetaGroup[] {
  const map = new Map<string, number>()
  for (const t of totals) {
    const name = metaByAppId.get(t.appId)?.[field]?.trim() || '__unknown__'
    map.set(name, (map.get(name) ?? 0) + t.seconds)
  }
  return [...map.entries()]
    .map(([name, seconds]) => ({ name, seconds }))
    .sort((a, b) => b.seconds - a.seconds)
}
