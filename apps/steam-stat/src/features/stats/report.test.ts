import type { SessionInput, StatsPrefs } from './index'
import { describe, expect, it } from 'vitest'
import dayjs from '@/utils/dayjs'
import { buildPeriodReport, buildYearReview, onThisDay } from './report'

const prefs: StatsPrefs = { dayStartHour: 0, weekStartsOn: 1 }

function at(s: string): number {
  return dayjs(s, 'YYYY-MM-DD HH:mm:ss').unix()
}
function session(appId: number, start: string, end: string): SessionInput {
  const s = at(start)
  const e = at(end)
  return { appId, startTime: s, endTime: e, duration: e - s, idleDuration: 0 }
}

const sessions = [
  // 上周
  session(1, '2024-01-08 10:00:00', '2024-01-08 11:00:00'),
  session(2, '2024-01-09 10:00:00', '2024-01-09 11:00:00'),
  // 本周
  session(1, '2024-01-15 10:00:00', '2024-01-15 12:00:00'),
  session(3, '2024-01-16 10:00:00', '2024-01-16 11:00:00'),
  // 去年今天
  session(4, '2023-06-15 20:00:00', '2023-06-15 22:00:00'),
]

describe('buildPeriodReport', () => {
  it('compares current week to previous equal span', () => {
    const from = at('2024-01-15 00:00:00')
    const to = at('2024-01-22 00:00:00')
    const r = buildPeriodReport(sessions, from, to, at('2024-01-08 00:00:00'), from, prefs)
    expect(r.seconds).toBe(10800)
    expect(r.prev.seconds).toBe(7200)
    expect(r.games).toBe(2)
    expect(r.topGames[0].appId).toBe(1)
    expect(r.newGames).toContain(3)
    expect(r.daily).toHaveLength(7)
    expect(r.busiestDay?.day).toBe('2024-01-15')
    expect(r.hourly[10]).toBeCloseTo(10800)
  })
})

describe('buildYearReview', () => {
  it('aggregates the year', () => {
    const r = buildYearReview(sessions, 2024, prefs)
    expect(r.games).toBe(3)
    expect(r.daysPlayed).toBe(4)
    expect(r.monthly[0]).toBe(18000)
    expect(r.busiestMonth).toBe(0)
    expect(r.topGame?.appId).toBe(1)
    expect(r.newGames).toBe(3)
    expect(r.longestSession?.appId).toBe(1)
  })
})

describe('onThisDay', () => {
  it('returns previous years for the same MM-DD', () => {
    const rows = onThisDay(sessions, '06-15', prefs, 2024)
    expect(rows).toHaveLength(1)
    expect(rows[0].year).toBe(2023)
    expect(rows[0].seconds).toBe(7200)
    expect(onThisDay(sessions, '01-15', prefs, 2024)).toHaveLength(0)
  })
})
