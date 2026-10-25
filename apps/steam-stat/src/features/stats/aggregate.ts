import type { Dayjs } from 'dayjs'
import type { StatApp, StatSession } from './model'
import type { Granularity, TimeOptions } from './time'
import dayjs from '@/utils/dayjs'
import {
  bucketKey,
  bucketKeysBetween,
  DAY_FORMAT,
  dayKey,
  dayKeysBetween,
  dayStartTs,
  lateClockMinutes,
  splitByDay,
  splitByHour,
  startOfWeek,
  weekdayIndex,
} from './time'

function zeros(length: number): number[] {
  return Array.from<number>({ length }).fill(0)
}

export interface SessionFilter {
  steamIds?: string[]
  /** [startTs, endTs]（含），按会话开始时间过滤 */
  range?: [number, number] | null
  includeApps?: number[]
  excludeApps?: number[]
  appId?: number | null
  /** 逻辑日 key（下钻） */
  day?: string | null
}

export function filterSessions(sessions: StatSession[], filter: SessionFilter, opts: TimeOptions): StatSession[] {
  const steamIds = filter.steamIds?.length ? new Set(filter.steamIds) : null
  const include = filter.includeApps?.length ? new Set(filter.includeApps) : null
  const exclude = filter.excludeApps?.length ? new Set(filter.excludeApps) : null
  return sessions.filter((s) => {
    if (steamIds && !steamIds.has(s.steamId)) {
      return false
    }
    if (filter.range && (s.start < filter.range[0] || s.start > filter.range[1])) {
      return false
    }
    if (include && !include.has(s.appId)) {
      return false
    }
    if (exclude?.has(s.appId)) {
      return false
    }
    if (filter.appId != null && s.appId !== filter.appId) {
      return false
    }
    if (filter.day && !splitByDay(s.start, s.duration, s.idle, opts).some(piece => piece.day === filter.day)) {
      return false
    }
    return true
  })
}

export interface Totals {
  count: number
  apps: number
  seconds: number
  idle: number
  effective: number
  avgSeconds: number
  activeDays: number
}

export function totals(sessions: StatSession[], opts: TimeOptions): Totals {
  let seconds = 0
  let idle = 0
  const apps = new Set<number>()
  const days = new Set<string>()
  for (const s of sessions) {
    seconds += s.duration
    idle += s.idle
    apps.add(s.appId)
    for (const piece of splitByDay(s.start, s.duration, s.idle, opts)) {
      days.add(piece.day)
    }
  }
  return {
    count: sessions.length,
    apps: apps.size,
    seconds,
    idle,
    effective: Math.max(0, seconds - idle),
    avgSeconds: sessions.length ? seconds / sessions.length : 0,
    activeDays: days.size,
  }
}

export interface AppAggregate {
  appId: number
  seconds: number
  idle: number
  count: number
  first: number
  last: number
}

export function byApp(sessions: StatSession[]): AppAggregate[] {
  const map = new Map<number, AppAggregate>()
  for (const s of sessions) {
    const item = map.get(s.appId)
    if (item) {
      item.seconds += s.duration
      item.idle += s.idle
      item.count += 1
      item.first = Math.min(item.first, s.start)
      item.last = Math.max(item.last, s.end)
    }
    else {
      map.set(s.appId, { appId: s.appId, seconds: s.duration, idle: s.idle, count: 1, first: s.start, last: s.end })
    }
  }
  return [...map.values()].sort((a, b) => b.seconds - a.seconds)
}

export interface SeriesResult {
  keys: string[]
  /** 每个桶的总秒数 */
  values: number[]
  /** 可选：按应用拆分（appId → 每桶秒数） */
  byApp: Map<number, number[]>
}

/** 时间序列：按粒度聚合（补零），可选按指定应用拆分为堆叠序列 */
export function timeSeries(
  sessions: StatSession[],
  fromKey: string,
  toKey: string,
  granularity: Granularity,
  opts: TimeOptions,
  splitApps: number[] = [],
): SeriesResult {
  const splitSet = new Set(splitApps)
  const keys = granularity === 'hour'
    ? dayKeysBetween(fromKey, toKey).flatMap(day => Array.from({ length: 24 }, (_, h) => `${day} ${String(h).padStart(2, '0')}`))
    : bucketKeysBetween(fromKey, toKey, granularity, opts)
  const index = new Map(keys.map((key, i) => [key, i]))
  const values = zeros(keys.length)
  const perApp = new Map<number, number[]>(splitApps.map(appId => [appId, zeros(keys.length)]))

  const add = (key: string, appId: number, seconds: number) => {
    const i = index.get(key)
    if (i === undefined) {
      return
    }
    values[i] += seconds
    if (splitSet.has(appId)) {
      perApp.get(appId)![i] += seconds
    }
  }

  for (const s of sessions) {
    if (granularity === 'hour') {
      for (const piece of splitByHour(s.start, s.duration)) {
        add(dayjs.unix(piece.hourStart).format('YYYY-MM-DD HH'), s.appId, piece.seconds)
      }
    }
    else {
      for (const piece of splitByDay(s.start, s.duration, s.idle, opts)) {
        add(bucketKey(piece.day, granularity, opts), s.appId, piece.seconds)
      }
    }
  }
  return { keys, values, byApp: perApp }
}

/** 逻辑日 → 秒数 */
export function dailyTotals(sessions: StatSession[], opts: TimeOptions): Map<string, number> {
  const map = new Map<string, number>()
  for (const s of sessions) {
    for (const piece of splitByDay(s.start, s.duration, s.idle, opts)) {
      map.set(piece.day, (map.get(piece.day) ?? 0) + piece.seconds)
    }
  }
  return map
}

/** 24 小时 × 星期 矩阵（秒），行 = 按一周起始日对齐的星期索引 */
export function hourWeekMatrix(sessions: StatSession[], opts: TimeOptions): number[][] {
  const matrix = Array.from({ length: 7 }, () => zeros(24))
  for (const s of sessions) {
    for (const piece of splitByHour(s.start, s.duration)) {
      const date = dayjs.unix(piece.hourStart)
      matrix[weekdayIndex(date, opts)][date.hour()] += piece.seconds
    }
  }
  return matrix
}

export interface GanttSegment {
  appId: number
  day: string
  /** 相对逻辑日起点的秒数 */
  from: number
  to: number
  start: number
  end: number
}

/** 甘特图：每个逻辑日一行，显示每段游玩的起止 */
export function ganttSegments(sessions: StatSession[], days: string[], opts: TimeOptions): GanttSegment[] {
  const daySet = new Set(days)
  const segments: GanttSegment[] = []
  for (const s of sessions) {
    for (const piece of splitByDay(s.start, s.duration, s.idle, opts)) {
      if (!daySet.has(piece.day)) {
        continue
      }
      const base = dayStartTs(piece.day, opts)
      segments.push({ appId: s.appId, day: piece.day, from: piece.start - base, to: piece.end - base, start: piece.start, end: piece.end })
    }
  }
  return segments
}

export interface LifecyclePoint {
  ts: number
  /** 自首次启动起的天数 */
  dayOffset: number
  seconds: number
}

/** 生命周期曲线：从首次启动起的累计时长（可叠加基线） */
export function lifecycle(sessions: StatSession[], appId: number, baselineSeconds = 0): LifecyclePoint[] {
  const own = sessions.filter(s => s.appId === appId).sort((a, b) => a.start - b.start)
  if (own.length === 0) {
    return []
  }
  const first = own[0].start
  const points: LifecyclePoint[] = [{ ts: first, dayOffset: 0, seconds: baselineSeconds }]
  let cumulative = baselineSeconds
  for (const s of own) {
    cumulative += s.duration
    points.push({ ts: s.end, dayOffset: (s.end - first) / 86400, seconds: cumulative })
  }
  return points
}

export interface StreakRecord {
  days: number
  from: string
  to: string
}

export interface PersonalRecords {
  longestStreak: StreakRecord | null
  longestBreak: StreakRecord | null
  currentStreak: StreakRecord | null
  longestSession: StatSession | null
  mostPlayedDay: { day: string, seconds: number } | null
  mostSessionsDay: { day: string, count: number } | null
  latestNight: { session: StatSession, minutes: number } | null
  earliestMorning: { session: StatSession, minutes: number } | null
  activeDays: number
  totalDays: number
}

/** 个人纪录：最长连续游玩 / 休息天数、最长单次会话、单日最多、最晚 / 最早游玩 */
export function personalRecords(sessions: StatSession[], opts: TimeOptions, todayKey = dayKey(dayjs().unix(), opts)): PersonalRecords {
  const daily = dailyTotals(sessions, opts)
  const sessionsPerDay = new Map<string, number>()
  for (const s of sessions) {
    const key = dayKey(s.start, opts)
    sessionsPerDay.set(key, (sessionsPerDay.get(key) ?? 0) + 1)
  }
  const days = [...daily.keys()].sort()
  const result: PersonalRecords = {
    longestStreak: null,
    longestBreak: null,
    currentStreak: null,
    longestSession: null,
    mostPlayedDay: null,
    mostSessionsDay: null,
    latestNight: null,
    earliestMorning: null,
    activeDays: days.length,
    totalDays: days.length ? dayKeysBetween(days[0], todayKey > days.at(-1)! ? todayKey : days.at(-1)!).length : 0,
  }
  if (days.length === 0) {
    return result
  }

  const diffDays = (a: string, b: string) => dayjs(b, DAY_FORMAT).diff(dayjs(a, DAY_FORMAT), 'day')
  let runStart = days[0]
  let prev = days[0]
  const consider = (from: string, to: string) => {
    const length = diffDays(from, to) + 1
    if (!result.longestStreak || length > result.longestStreak.days) {
      result.longestStreak = { days: length, from, to }
    }
  }
  for (const day of days.slice(1)) {
    const gap = diffDays(prev, day)
    if (gap > 1) {
      consider(runStart, prev)
      const breakDays = gap - 1
      if (!result.longestBreak || breakDays > result.longestBreak.days) {
        result.longestBreak = {
          days: breakDays,
          from: dayjs(prev, DAY_FORMAT).add(1, 'day').format(DAY_FORMAT),
          to: dayjs(day, DAY_FORMAT).subtract(1, 'day').format(DAY_FORMAT),
        }
      }
      runStart = day
    }
    prev = day
  }
  consider(runStart, prev)
  const sinceLast = diffDays(prev, todayKey)
  if (sinceLast <= 1) {
    result.currentStreak = { days: diffDays(runStart, prev) + 1, from: runStart, to: prev }
  }
  else if (!result.longestBreak || sinceLast - 1 > result.longestBreak.days) {
    result.longestBreak = {
      days: sinceLast - 1,
      from: dayjs(prev, DAY_FORMAT).add(1, 'day').format(DAY_FORMAT),
      to: dayjs(todayKey, DAY_FORMAT).subtract(1, 'day').format(DAY_FORMAT),
    }
  }

  for (const [day, seconds] of daily) {
    if (!result.mostPlayedDay || seconds > result.mostPlayedDay.seconds) {
      result.mostPlayedDay = { day, seconds }
    }
  }
  for (const [day, count] of sessionsPerDay) {
    if (!result.mostSessionsDay || count > result.mostSessionsDay.count) {
      result.mostSessionsDay = { day, count }
    }
  }
  for (const s of sessions) {
    if (!result.longestSession || s.duration > result.longestSession.duration) {
      result.longestSession = s
    }
    const late = lateClockMinutes(s.end, opts)
    if (!result.latestNight || late > result.latestNight.minutes) {
      result.latestNight = { session: s, minutes: late }
    }
    const early = lateClockMinutes(s.start, opts)
    if (early < 24 * 60 && (!result.earliestMorning || early < result.earliestMorning.minutes)) {
      result.earliestMorning = { session: s, minutes: early }
    }
  }
  return result
}

export type PeriodUnit = 'week' | 'month' | 'year'

export interface PeriodRange {
  unit: PeriodUnit
  /** 起始逻辑日 key */
  from: string
  /** 结束逻辑日 key（含） */
  to: string
}

/** 锚点日所在周期 */
export function periodOf(anchorKey: string, unit: PeriodUnit, opts: TimeOptions): PeriodRange {
  const anchor = dayjs(anchorKey, DAY_FORMAT)
  const start = unit === 'week' ? startOfWeek(anchor, opts) : anchor.startOf(unit)
  const end = unit === 'week' ? start.add(6, 'day') : anchor.endOf(unit)
  return { unit, from: start.format(DAY_FORMAT), to: end.format(DAY_FORMAT) }
}

export function shiftPeriod(period: PeriodRange, delta: number, opts: TimeOptions): PeriodRange {
  const anchor = dayjs(period.from, DAY_FORMAT).add(delta, period.unit)
  return periodOf(anchor.format(DAY_FORMAT), period.unit, opts)
}

export function rangeOfDays(from: string, to: string, opts: TimeOptions): [number, number] {
  return [dayStartTs(from, opts), dayStartTs(dayjs(to, DAY_FORMAT).add(1, 'day').format(DAY_FORMAT), opts) - 1]
}

export function sessionsInDays(sessions: StatSession[], from: string, to: string, opts: TimeOptions): StatSession[] {
  const [start, end] = rangeOfDays(from, to, opts)
  return sessions.filter(s => s.start >= start && s.start <= end)
}

export interface Comparison {
  key: 'week' | 'month' | 'yearOverYear'
  current: { from: string, to: string, totals: Totals }
  previous: { from: string, to: string, totals: Totals }
}

/** 同比 / 环比：本周对上周、本月对上月、本月对去年同月（均截至今天的相同天数） */
export function comparePeriods(sessions: StatSession[], todayKey: string, opts: TimeOptions): Comparison[] {
  const today = dayjs(todayKey, DAY_FORMAT)
  const build = (key: Comparison['key'], from: Dayjs, previousFrom: Dayjs): Comparison => {
    const elapsed = today.diff(from, 'day')
    const to = today
    const previousTo = previousFrom.add(elapsed, 'day')
    const cur = sessionsInDays(sessions, from.format(DAY_FORMAT), to.format(DAY_FORMAT), opts)
    const prev = sessionsInDays(sessions, previousFrom.format(DAY_FORMAT), previousTo.format(DAY_FORMAT), opts)
    return {
      key,
      current: { from: from.format(DAY_FORMAT), to: to.format(DAY_FORMAT), totals: totals(cur, opts) },
      previous: { from: previousFrom.format(DAY_FORMAT), to: previousTo.format(DAY_FORMAT), totals: totals(prev, opts) },
    }
  }
  const weekStart = startOfWeek(today, opts)
  const monthStart = today.startOf('month')
  return [
    build('week', weekStart, weekStart.subtract(1, 'week')),
    build('month', monthStart, monthStart.subtract(1, 'month')),
    build('yearOverYear', monthStart, monthStart.subtract(1, 'year')),
  ]
}

/** 变化率（previous 为 0 时返回 null） */
export function changeRatio(current: number, previous: number): number | null {
  if (previous === 0) {
    return current === 0 ? 0 : null
  }
  return (current - previous) / previous
}

export interface PeriodReport {
  period: PeriodRange
  totals: Totals
  previousTotals: Totals
  topApps: AppAggregate[]
  newApps: number[]
  busiestDay: { day: string, seconds: number } | null
  longestSession: StatSession | null
  daily: { keys: string[], values: number[] }
  hourDistribution: number[]
  weekdayDistribution: number[]
  records: PersonalRecords
}

/** 周报 / 月报 / 年度回顾 */
export function periodReport(all: StatSession[], period: PeriodRange, opts: TimeOptions): PeriodReport {
  const sessions = sessionsInDays(all, period.from, period.to, opts)
  const previous = shiftPeriod(period, -1, opts)
  const previousSessions = sessionsInDays(all, previous.from, previous.to, opts)
  const firstSeen = new Map<number, number>()
  for (const s of all) {
    if (!firstSeen.has(s.appId)) {
      firstSeen.set(s.appId, s.start)
    }
  }
  const [start, end] = rangeOfDays(period.from, period.to, opts)
  const series = timeSeries(sessions, period.from, period.to, period.unit === 'year' ? 'month' : 'day', opts)
  const daily = dailyTotals(sessions, opts)
  let busiestDay: PeriodReport['busiestDay'] = null
  for (const [day, seconds] of daily) {
    if (day >= period.from && day <= period.to && (!busiestDay || seconds > busiestDay.seconds)) {
      busiestDay = { day, seconds }
    }
  }
  const hourDistribution = zeros(24)
  const weekdayDistribution = zeros(7)
  const matrix = hourWeekMatrix(sessions, opts)
  matrix.forEach((row, weekday) => row.forEach((seconds, hour) => {
    hourDistribution[hour] += seconds
    weekdayDistribution[weekday] += seconds
  }))
  return {
    period,
    totals: totals(sessions, opts),
    previousTotals: totals(previousSessions, opts),
    topApps: byApp(sessions),
    newApps: [...firstSeen.entries()].filter(([, ts]) => ts >= start && ts <= end).map(([appId]) => appId),
    busiestDay,
    longestSession: sessions.reduce<StatSession | null>((best, s) => (!best || s.duration > best.duration ? s : best), null),
    daily: { keys: series.keys, values: series.values },
    hourDistribution,
    weekdayDistribution,
    records: personalRecords(sessions, opts, period.to),
  }
}

export interface OnThisDayEntry {
  year: number
  day: string
  seconds: number
  sessions: StatSession[]
  apps: AppAggregate[]
}

/** 历史上的今天：往年同月同日的游玩 */
export function onThisDay(sessions: StatSession[], todayKey: string, opts: TimeOptions): OnThisDayEntry[] {
  const today = dayjs(todayKey, DAY_FORMAT)
  const monthDay = today.format('MM-DD')
  const grouped = new Map<string, StatSession[]>()
  for (const s of sessions) {
    for (const piece of splitByDay(s.start, s.duration, s.idle, opts)) {
      if (piece.day.slice(5) === monthDay && piece.day < todayKey) {
        const list = grouped.get(piece.day) ?? []
        if (!list.includes(s)) {
          list.push(s)
        }
        grouped.set(piece.day, list)
      }
    }
  }
  return [...grouped.entries()]
    .map(([day, list]) => ({
      year: Number(day.slice(0, 4)),
      day,
      seconds: list.reduce((sum, s) => sum + s.duration, 0),
      sessions: list,
      apps: byApp(list),
    }))
    .sort((a, b) => b.year - a.year)
}

export type MetaField = 'developer' | 'publisher' | 'type'

/** 按开发商 / 发行商 / 类型汇总时长 */
export function metaBreakdown(sessions: StatSession[], apps: Map<number, StatApp>, field: MetaField, unknownLabel: string): { name: string, seconds: number }[] {
  const map = new Map<string, number>()
  for (const s of sessions) {
    const raw = apps.get(s.appId)?.[field]
    const names = raw ? raw.split(/[,;]/).map(name => name.trim()).filter(Boolean) : [unknownLabel]
    for (const name of names) {
      map.set(name, (map.get(name) ?? 0) + s.duration / names.length)
    }
  }
  return [...map.entries()].map(([name, seconds]) => ({ name, seconds })).sort((a, b) => b.seconds - a.seconds)
}
