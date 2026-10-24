import type { SessionInput, StatsPrefs } from './index'
import { describe, expect, it } from 'vitest'
import dayjs from '@/utils/dayjs'
import {
  bucketSeries,
  clipSessions,
  comparePeriods,
  lifecycle,
  logicalDayKey,
  personalRecords,

  splitByLogicalDay,

} from './index'
import { oklchToHex } from './palette'

const prefs: StatsPrefs = { dayStartHour: 0, weekStartsOn: 1 }

function at(s: string): number {
  return dayjs(s, 'YYYY-MM-DD HH:mm:ss').unix()
}

function session(appId: number, start: string, end: string, duration?: number): SessionInput {
  const s = at(start)
  const e = at(end)
  return { appId, startTime: s, endTime: e, duration: duration ?? e - s, idleDuration: 0 }
}

describe('logicalDayKey', () => {
  it('assigns early-morning time to previous day when dayStartHour=4', () => {
    const p = { dayStartHour: 4, weekStartsOn: 1 } as StatsPrefs
    expect(logicalDayKey(at('2024-01-10 02:00:00'), p)).toBe('2024-01-09')
    expect(logicalDayKey(at('2024-01-10 05:00:00'), p)).toBe('2024-01-10')
    expect(logicalDayKey(at('2024-01-10 02:00:00'), prefs)).toBe('2024-01-10')
  })
})

describe('clipSessions', () => {
  it('clips span and scales duration proportionally', () => {
    const s = session(1, '2024-01-01 10:00:00', '2024-01-01 14:00:00', 7200)
    const [p] = clipSessions([s], at('2024-01-01 11:00:00'), at('2024-01-01 13:00:00'))
    expect(p.duration).toBeCloseTo(3600)
    expect(p.isStartPiece).toBe(false)
    const [q] = clipSessions([s], at('2024-01-01 09:00:00'), at('2024-01-01 10:30:00'))
    expect(q.isStartPiece).toBe(true)
  })
})

describe('splitByLogicalDay', () => {
  it('splits a session across midnight', () => {
    const s = session(1, '2024-01-01 23:00:00', '2024-01-02 01:00:00')
    const map = splitByLogicalDay([s], prefs)
    expect(map.get('2024-01-01')).toBeCloseTo(3600)
    expect(map.get('2024-01-02')).toBeCloseTo(3600)
  })
})

describe('bucketSeries', () => {
  it('week buckets respect weekStartsOn', () => {
    // 2024-01-01 是周一
    const sessions = [session(1, '2024-01-07 10:00:00', '2024-01-07 12:00:00')]
    const from = at('2024-01-01 00:00:00')
    const to = at('2024-01-15 00:00:00')
    const monday = bucketSeries(sessions, 'week', from, to, { dayStartHour: 0, weekStartsOn: 1 })
    expect(monday[0].seconds).toBeCloseTo(7200) // 周日归本周(周一始)
    const sunday = bucketSeries(sessions, 'week', from, to, { dayStartHour: 0, weekStartsOn: 0 })
    expect(sunday[1].seconds).toBeCloseTo(7200) // 周日始则落入下一周
  })
})

describe('personalRecords', () => {
  it('computes streaks, breaks and latest night', () => {
    const now = at('2024-01-10 12:00:00')
    const sessions = [
      session(1, '2024-01-01 10:00:00', '2024-01-01 12:00:00'),
      session(1, '2024-01-02 10:00:00', '2024-01-02 12:00:00'),
      session(1, '2024-01-03 22:00:00', '2024-01-04 01:30:00'),
      session(1, '2024-01-08 10:00:00', '2024-01-08 11:00:00'),
    ]
    const r = personalRecords(sessions, prefs, now)
    // 游玩日: 01-01..01-04（跨夜会话计入 01-04）、01-08 → 最长连续 4 天
    expect(r.longestStreak?.days).toBe(4)
    expect(r.longestStreak?.from).toBe('2024-01-01')
    // 空窗取最长且 to 为最后一个未游玩日
    expect(r.longestBreak).not.toBeNull()
    expect(r.longestBreak!.to).not.toBe('2024-01-04')
    // 熬夜深度 = end − 起始逻辑日起点：22:00→01:30 跨 3.5h 最深
    expect(r.latestNight?.clock).toBe('01:30')
    expect(r.mostGamesDay?.count).toBe(1)
  })
})

describe('comparePeriods', () => {
  it('previous window has equal elapsed length', () => {
    const now = at('2024-01-10 12:00:00') // 周三
    const sessions = [
      // 本周 3600s
      session(1, '2024-01-09 10:00:00', '2024-01-09 11:00:00'),
      // 上周等长区间（周一0点 → 周三12点）内 7200s
      session(1, '2024-01-02 10:00:00', '2024-01-02 12:00:00'),
      // 上周等长区间之外 9999s（不应计入）
      session(1, '2024-01-05 10:00:00', '2024-01-05 11:00:00'),
    ]
    const { week } = comparePeriods(sessions, now, prefs)
    expect(week.current.seconds).toBeCloseTo(3600)
    expect(week.previous.seconds).toBeCloseTo(7200)
  })
})

describe('lifecycle', () => {
  it('starts at first session start with baseline value', () => {
    const sessions = [
      session(1, '2024-01-01 10:00:00', '2024-01-01 11:00:00'),
      session(1, '2024-01-02 10:00:00', '2024-01-02 11:30:00'),
    ]
    const points = lifecycle(sessions, [1], new Map([[1, 7200]])).get(1)!
    expect(points[0].time).toBe(at('2024-01-01 10:00:00'))
    expect(points[0].cumulative).toBe(7200)
    expect(points[2].cumulative).toBe(7200 + 3600 + 5400)
  })
})

describe('latestNight', () => {
  it('dayStartHour=4: 22:00→03:00 beats 14:00→23:50', () => {
    const p = { dayStartHour: 4, weekStartsOn: 1 } as StatsPrefs
    const sessions = [
      session(1, '2024-01-10 14:00:00', '2024-01-10 23:50:00'),
      session(2, '2024-01-11 22:00:00', '2024-01-12 03:00:00'),
    ]
    const r = personalRecords(sessions, p, at('2024-01-20 12:00:00'))
    expect(r.latestNight?.clock).toBe('03:00')
  })
  it('dayStartHour=0: 21:00→01:30 beats 20:00→23:00', () => {
    const sessions = [
      session(1, '2024-01-10 20:00:00', '2024-01-10 23:00:00'),
      session(2, '2024-01-11 21:00:00', '2024-01-12 01:30:00'),
    ]
    const r = personalRecords(sessions, prefs, at('2024-01-20 12:00:00'))
    expect(r.latestNight?.clock).toBe('01:30')
  })
})

describe('nextDayKey / DST', () => {
  it('iterates 25h fall-back days without looping', async () => {
    const { nextDayKey } = await import('./index')
    expect(nextDayKey('2024-10-27')).toBe('2024-10-28')
    // 遍历 10 天必须终止且产生 10 个桶
    const from = at('2024-10-26 00:00:00')
    const to = at('2024-11-05 00:00:00')
    const buckets = bucketSeries([], 'day', from, to, prefs)
    expect(buckets.length).toBeGreaterThanOrEqual(9)
    expect(buckets.length).toBeLessThanOrEqual(11)
  })
})

describe('oklchToHex', () => {
  it('converts oklch red to #ff0000', () => {
    const hex = oklchToHex(0.6279, 0.2577, 29.23)
    const r = Number.parseInt(hex.slice(1, 3), 16)
    const g = Number.parseInt(hex.slice(3, 5), 16)
    const b = Number.parseInt(hex.slice(5, 7), 16)
    expect(Math.abs(r - 255)).toBeLessThanOrEqual(2)
    expect(g).toBeLessThanOrEqual(2)
    expect(b).toBeLessThanOrEqual(2)
  })
})
