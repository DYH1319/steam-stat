import type { SessionInput, StatsPrefs } from './index'
import dayjs from '@/utils/dayjs'
import {
  clipSessions,
  logicalDayKey,
  logicalDayStart,
  nextDayKey,

  splitByLogicalDay,

} from './index'

export interface PeriodReport {
  seconds: number
  effectiveSeconds: number
  sessions: number
  games: number
  prev: { seconds: number, sessions: number, games: number }
  topGames: { appId: number, seconds: number, count: number }[]
  daily: { day: string, seconds: number }[]
  hourly: number[]
  newGames: number[]
  longestSession: SessionInput | null
  busiestDay: { day: string, seconds: number } | null
}

function statsFor(sessions: SessionInput[], from: number, to: number) {
  const pieces = clipSessions(sessions, from, to)
  return {
    seconds: pieces.reduce((s, p) => s + p.duration, 0),
    effectiveSeconds: pieces.reduce((s, p) => s + Math.max(0, p.duration - p.idleDuration), 0),
    sessions: pieces.filter(p => p.isStartPiece).length,
    games: new Set(pieces.map(p => p.appId)).size,
  }
}

/** 周/月报：当前区间 vs 上一区间；newGames 需传入全量会话判断首次游玩 */
export function buildPeriodReport(
  allSessions: SessionInput[],
  from: number,
  to: number,
  prevFrom: number,
  prevTo: number,
  prefs: StatsPrefs,
): PeriodReport {
  const current = statsFor(allSessions, from, to)
  const prev = statsFor(allSessions, prevFrom, prevTo)
  const pieces = clipSessions(allSessions, from, to)

  const byApp = new Map<number, { appId: number, seconds: number, count: number }>()
  const hourly: number[] = Array.from<number>({ length: 24 }).fill(0)
  let longest: SessionInput | null = null
  for (const p of pieces) {
    const t = byApp.get(p.appId) ?? { appId: p.appId, seconds: 0, count: 0 }
    t.seconds += p.duration
    if (p.isStartPiece) {
      t.count += 1
      hourly[dayjs.unix(p.startTime).hour()] += p.duration
    }
    byApp.set(p.appId, t)
    if (p.session.duration! > (longest?.duration ?? -1)) {
      longest = p.session
    }
  }
  const topGames = [...byApp.values()].sort((a, b) => b.seconds - a.seconds).slice(0, 5)

  const byDay = splitByLogicalDay(clipSessions(allSessions, from, to).map(p => p.session), prefs)
  const daily: { day: string, seconds: number }[] = []
  let busiestDay: { day: string, seconds: number } | null = null
  for (let key = logicalDayKey(from, prefs); logicalDayKey(to - 1, prefs) >= key; key = nextDayKey(key)) {
    const seconds = byDay.get(key) ?? 0
    daily.push({ day: key, seconds })
    if (seconds > (busiestDay?.seconds ?? 0)) {
      busiestDay = { day: key, seconds }
    }
    if (nextDayKey(key) <= key) {
      break // DST 防死循环保险
    }
  }

  const firstSeen = new Map<number, number>()
  for (const s of allSessions) {
    if (s.endTime != null && s.duration != null && s.duration > 0) {
      firstSeen.set(s.appId, Math.min(firstSeen.get(s.appId) ?? Number.MAX_SAFE_INTEGER, s.startTime))
    }
  }
  const newGames = [...firstSeen.entries()].filter(([, t0]) => t0 >= from && t0 < to).map(([id]) => id)

  return {
    ...current,
    prev: { seconds: prev.seconds, sessions: prev.sessions, games: prev.games },
    topGames,
    daily,
    hourly,
    newGames,
    longestSession: longest,
    busiestDay,
  }
}

export interface YearReview {
  seconds: number
  daysPlayed: number
  sessions: number
  games: number
  topGame: { appId: number, seconds: number } | null
  top5: { appId: number, seconds: number }[]
  monthly: number[]
  busiestMonth: number | null
  longestStreak: { days: number, from: string, to: string } | null
  latestNight: { clock: string, appId: number } | null
  longestSession: SessionInput | null
  newGames: number
  firstSession: SessionInput | null
  calendar: [string, number][]
}

export function buildYearReview(allSessions: SessionInput[], year: number, prefs: StatsPrefs): YearReview {
  const yearStr = String(year)
  const sessions = allSessions.filter(s =>
    s.endTime != null && s.duration != null && s.duration > 0
    && logicalDayKey(s.startTime, prefs).startsWith(yearStr),
  )
  const byDay = splitByLogicalDay(sessions, prefs)
  const calendar = [...byDay.entries()].map(([k, s]) => [k, s] as [string, number]).sort()

  const byApp = new Map<number, number>()
  const monthly: number[] = Array.from<number>({ length: 12 }).fill(0)
  const firstSeen = new Map<number, number>()
  let longest: SessionInput | null = null
  let first: SessionInput | null = null
  let nightBest = -1
  let night: { clock: string, appId: number } | null = null
  for (const s of sessions) {
    byApp.set(s.appId, (byApp.get(s.appId) ?? 0) + s.duration!)
    monthly[dayjs.unix(s.startTime).month()] += s.duration!
    firstSeen.set(s.appId, Math.min(firstSeen.get(s.appId) ?? Number.MAX_SAFE_INTEGER, s.startTime))
    if (s.duration! > (longest?.duration ?? -1)) {
      longest = s
    }
    if (s.startTime < (first?.startTime ?? Number.MAX_SAFE_INTEGER)) {
      first = s
    }
    const n = s.endTime! - logicalDayStart(logicalDayKey(s.startTime, prefs), prefs)
    if (n > nightBest) {
      nightBest = n
      night = { clock: dayjs.unix(s.endTime!).format('HH:mm'), appId: s.appId }
    }
  }
  const top5 = [...byApp.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5).map(([appId, seconds]) => ({ appId, seconds }))

  let streak = 0
  let streakFrom = ''
  let best: { days: number, from: string, to: string } | null = null
  const played = new Set(byDay.keys())
  for (let key = `${yearStr}-01-01`; key <= `${yearStr}-12-31`; key = nextDayKey(key)) {
    if (played.has(key)) {
      if (streak === 0) {
        streakFrom = key
      }
      streak++
      if (streak > (best?.days ?? 0)) {
        best = { days: streak, from: streakFrom, to: key }
      }
    }
    else {
      streak = 0
    }
    if (nextDayKey(key) <= key) {
      break
    }
  }

  const busiestMonth = monthly.indexOf(Math.max(...monthly))
  const firstEver = new Map<number, number>()
  for (const s of allSessions) {
    if (s.endTime != null && s.duration != null && s.duration > 0) {
      firstEver.set(s.appId, Math.min(firstEver.get(s.appId) ?? Number.MAX_SAFE_INTEGER, s.startTime))
    }
  }
  const newGames = [...firstEver.values()].filter(t0 => logicalDayKey(t0, prefs).startsWith(yearStr)).length

  return {
    seconds: sessions.reduce((s, x) => s + x.duration!, 0),
    daysPlayed: byDay.size,
    sessions: sessions.length,
    games: byApp.size,
    topGame: top5[0] ?? null,
    top5,
    monthly,
    busiestMonth: monthly[busiestMonth] > 0 ? busiestMonth : null,
    longestStreak: best,
    latestNight: night,
    longestSession: longest,
    newGames,
    firstSession: first,
    calendar,
  }
}

/** 历史上的今天：往年同一 MM-DD 的会话 */
export function onThisDay(
  allSessions: SessionInput[],
  monthDay: string,
  prefs: StatsPrefs,
  nowYear: number,
): { year: number, seconds: number, sessions: SessionInput[] }[] {
  const groups = new Map<number, SessionInput[]>()
  for (const s of allSessions) {
    if (s.endTime == null || s.duration == null || s.duration <= 0) {
      continue
    }
    const key = logicalDayKey(s.startTime, prefs)
    const year = Number(key.slice(0, 4))
    if (key.slice(5) === monthDay && year < nowYear) {
      const list = groups.get(year) ?? []
      list.push(s)
      groups.set(year, list)
    }
  }
  return [...groups.entries()]
    .sort((a, b) => b[0] - a[0])
    .map(([year, sessions]) => ({
      year,
      seconds: sessions.reduce((s, x) => s + x.duration!, 0),
      sessions: sessions.sort((a, b) => b.startTime - a.startTime),
    }))
}
