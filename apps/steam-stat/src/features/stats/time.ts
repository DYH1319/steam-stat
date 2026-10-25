import type { Dayjs } from 'dayjs'
import dayjs from '@/utils/dayjs'

/** 时间口径：一天的分界点（如凌晨 4 点前算前一天）与一周起始日 */
export interface TimeOptions {
  /** 0~6：该小时之前的时间计入前一天 */
  dayBoundaryHour: number
  /** 0 = 周日，1 = 周一 */
  weekStart: 0 | 1
}

export type Granularity = 'hour' | 'day' | 'week' | 'month'

export const DAY_FORMAT = 'YYYY-MM-DD'

/** 时间戳所在的逻辑日（YYYY-MM-DD） */
export function dayKey(ts: number, opts: TimeOptions): string {
  return dayjs.unix(ts).subtract(opts.dayBoundaryHour, 'hour').format(DAY_FORMAT)
}

/** 逻辑日 key 对应的起始时间戳（含分界点偏移） */
export function dayStartTs(key: string, opts: TimeOptions): number {
  return dayjs(key, DAY_FORMAT).add(opts.dayBoundaryHour, 'hour').unix()
}

/** 逻辑日 key 的下一天 key（按日历日递增，DST 安全） */
export function nextDayKey(key: string): string {
  return dayjs(key, DAY_FORMAT).add(1, 'day').format(DAY_FORMAT)
}

/** 包含两端的逻辑日 key 列表 */
export function dayKeysBetween(fromKey: string, toKey: string): string[] {
  const keys: string[] = []
  let cursor = dayjs(fromKey, DAY_FORMAT)
  const end = dayjs(toKey, DAY_FORMAT)
  while (!cursor.isAfter(end, 'day')) {
    keys.push(cursor.format(DAY_FORMAT))
    cursor = cursor.add(1, 'day')
  }
  return keys
}

/** 日期所在周的周起始日 */
export function startOfWeek(date: Dayjs, opts: TimeOptions): Dayjs {
  const offset = (date.day() - opts.weekStart + 7) % 7
  return date.startOf('day').subtract(offset, 'day')
}

/** 星期索引（按一周起始日对齐，0 = 一周第一天） */
export function weekdayIndex(date: Dayjs, opts: TimeOptions): number {
  return (date.day() - opts.weekStart + 7) % 7
}

/** 逻辑日 key 归属的聚合桶 key */
export function bucketKey(day: string, granularity: Exclude<Granularity, 'hour'>, opts: TimeOptions): string {
  const date = dayjs(day, DAY_FORMAT)
  switch (granularity) {
    case 'week': return startOfWeek(date, opts).format(DAY_FORMAT)
    case 'month': return date.format('YYYY-MM')
    default: return day
  }
}

/** 区间 [from, to] 内按粒度生成的全部桶 key（用于补零） */
export function bucketKeysBetween(fromKey: string, toKey: string, granularity: Exclude<Granularity, 'hour'>, opts: TimeOptions): string[] {
  const keys = new Set<string>()
  for (const day of dayKeysBetween(fromKey, toKey)) {
    keys.add(bucketKey(day, granularity, opts))
  }
  return [...keys]
}

export interface DayPiece {
  day: string
  start: number
  end: number
  seconds: number
  idle: number
}

/**
 * 将一段会话按逻辑日切分（跨越分界点的会话拆成多段，时长与挂机时长按比例分配）。
 */
export function splitByDay(start: number, duration: number, idle: number, opts: TimeOptions): DayPiece[] {
  const end = start + duration
  if (duration <= 0) {
    return []
  }
  const pieces: DayPiece[] = []
  let cursor = start
  while (cursor < end) {
    const day = dayKey(cursor, opts)
    const boundary = dayStartTs(nextDayKey(day), opts)
    const pieceEnd = Math.min(end, boundary > cursor ? boundary : cursor + 86400)
    const seconds = pieceEnd - cursor
    pieces.push({ day, start: cursor, end: pieceEnd, seconds, idle: idle * seconds / duration })
    cursor = pieceEnd
  }
  return pieces
}

/** 将一段会话按整点小时切分（时钟时间，用于作息热力图） */
export function splitByHour(start: number, duration: number): { hourStart: number, seconds: number }[] {
  const end = start + duration
  const pieces: { hourStart: number, seconds: number }[] = []
  let cursor = start
  while (cursor < end) {
    const hourStart = dayjs.unix(cursor).startOf('hour').unix()
    const next = Math.min(end, hourStart + 3600)
    pieces.push({ hourStart, seconds: next - cursor })
    cursor = next
  }
  return pieces
}

/** 一天中的「时刻」：分界点之前的时间记为 24 点之后（用于「最晚游玩」） */
export function lateClockMinutes(ts: number, opts: TimeOptions): number {
  const date = dayjs.unix(ts)
  const minutes = date.hour() * 60 + date.minute()
  return date.hour() < Math.max(opts.dayBoundaryHour, 6) ? minutes + 24 * 60 : minutes
}
