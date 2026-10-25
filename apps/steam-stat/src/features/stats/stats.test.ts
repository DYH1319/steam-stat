import type { StatSession } from './model'
import type { TimeOptions } from './time'
import { describe, expect, it } from 'vitest'
import dayjs from '@/utils/dayjs'
import { comparePeriods, periodOf, personalRecords, timeSeries } from './aggregate'
import { formatDuration } from './format'
import { decodeDataset } from './model'
import { dayKey, splitByDay, startOfWeek } from './time'

const opts: TimeOptions = { dayBoundaryHour: 4, weekStart: 1 }
const at = (value: string) => dayjs(value, 'YYYY-MM-DD HH:mm').unix()

function session(appId: number, start: string, minutes: number): StatSession {
  const s = at(start)
  return { appId, steamId: '1', start: s, duration: minutes * 60, idle: 0, end: s + minutes * 60 }
}

describe('time', () => {
  it('counts time before the day boundary as the previous day', () => {
    expect(dayKey(at('2026-03-02 03:59'), opts)).toBe('2026-03-01')
    expect(dayKey(at('2026-03-02 04:00'), opts)).toBe('2026-03-02')
  })

  it('splits sessions across the logical day boundary', () => {
    const pieces = splitByDay(at('2026-03-02 03:00'), 2 * 3600, 600, opts)
    expect(pieces.map(p => [p.day, p.seconds])).toEqual([['2026-03-01', 3600], ['2026-03-02', 3600]])
    expect(pieces[0].idle).toBe(300)
  })

  it('aligns weeks to the configured start day', () => {
    const sunday = dayjs('2026-03-08', 'YYYY-MM-DD')
    expect(startOfWeek(sunday, opts).format('YYYY-MM-DD')).toBe('2026-03-02')
    expect(startOfWeek(sunday, { ...opts, weekStart: 0 }).format('YYYY-MM-DD')).toBe('2026-03-08')
  })
})

describe('aggregate', () => {
  const sessions = [
    session(1, '2026-03-01 20:00', 60),
    session(2, '2026-03-02 21:00', 120),
    session(1, '2026-03-03 23:30', 90),
    session(1, '2026-03-07 10:00', 30),
  ]

  it('fills empty buckets and splits stacked apps', () => {
    const series = timeSeries(sessions, '2026-03-01', '2026-03-07', 'day', opts, [1])
    expect(series.keys).toHaveLength(7)
    expect(series.values.map(v => v / 60)).toEqual([60, 120, 90, 0, 0, 0, 30])
    expect(series.byApp.get(1)!.map(v => v / 60)).toEqual([60, 0, 90, 0, 0, 0, 30])
  })

  it('finds streaks, breaks and the latest night', () => {
    const records = personalRecords(sessions, opts, '2026-03-07')
    expect(records.longestStreak).toEqual({ days: 3, from: '2026-03-01', to: '2026-03-03' })
    expect(records.longestBreak).toEqual({ days: 3, from: '2026-03-04', to: '2026-03-06' })
    expect(records.currentStreak?.days).toBe(1)
    expect(records.longestSession?.appId).toBe(2)
    expect(records.latestNight?.session.start).toBe(at('2026-03-03 23:30'))
  })

  it('compares the current week with the same span of the previous week', () => {
    const [week] = comparePeriods(sessions, '2026-03-03', opts)
    expect(week.current.from).toBe('2026-03-02')
    expect(week.previous.from).toBe('2026-02-23')
    expect(week.current.totals.seconds).toBe(210 * 60)
    expect(week.previous.to).toBe('2026-02-24')
    expect(week.previous.totals.seconds).toBe(0)
  })

  it('resolves report periods', () => {
    expect(periodOf('2026-03-05', 'week', opts)).toEqual({ unit: 'week', from: '2026-03-02', to: '2026-03-08' })
    expect(periodOf('2026-03-05', 'month', opts).to).toBe('2026-03-31')
  })
})

describe('decode & format', () => {
  it('decodes flattened pages', () => {
    const data = decodeDataset([{
      users: [{ steamId: 'a' }, { steamId: 'b' }],
      apps: [{ appId: 7, name: null, installed: false }],
      baselines: [],
      sessions: [7, 1, 100, 50, 5],
      total: 1,
      lastUpdateTime: 9,
    }])
    expect(data.sessions).toEqual([{ appId: 7, steamId: 'b', start: 100, duration: 50, idle: 5, end: 150 }])
    expect(data.apps.get(7)?.name).toBe('App 7')
  })

  it('formats durations in all styles', () => {
    expect(formatDuration(482_100, 'hours')).toBe('133.9h')
    expect(formatDuration(482_100, 'hm')).toBe('133h 55m')
    expect(formatDuration(482_100, 'dh', { day: '天', hour: '小时', minute: '分' })).toBe('5天 13小时')
  })
})
