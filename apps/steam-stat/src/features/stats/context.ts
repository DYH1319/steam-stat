import type { ComputedRef, InjectionKey, Ref } from 'vue'
import type { StatsData, StatSession } from './model'
import type { TimeOptions } from './time'
import type { ChartTheme } from '@/composables/useChartTheme'
import { useChartTheme } from '@/composables/useChartTheme'
import { useStatsStore } from '@/store/modules/stats'
import { filterSessions, rangeOfDays } from './aggregate'
import { appName as resolveAppName } from './model'
import { createColorMap } from './palette'

export interface DrillState {
  appId: number | null
  day: string | null
}

export interface StatsContext {
  data: ComputedRef<StatsData>
  /** 用户 / 应用筛选（不含日期范围与下钻），用于纪录、对比、生命周期 */
  scoped: ComputedRef<StatSession[]>
  /** 用户 / 应用 / 日期范围筛选（不含下钻） */
  base: ComputedRef<StatSession[]>
  /** base + 下钻 */
  sessions: ComputedRef<StatSession[]>
  range: ComputedRef<{ from: string, to: string }>
  drill: Ref<DrillState>
  /** 固定的游戏（单游戏详情页） */
  fixedAppId: ComputedRef<number | null>
  opts: ComputedRef<TimeOptions>
  theme: ComputedRef<ChartTheme>
  topN: Ref<number>
  fmt: (seconds: number) => string
  appName: (appId: number) => string
  colorOf: (appId: number) => string
  setDrillApp: (appId: number | null) => void
  setDrillDay: (day: string | null) => void
  clearDrill: () => void
}

const STATS_CONTEXT_KEY: InjectionKey<StatsContext> = Symbol('stats-context')

/**
 * 创建并提供统计上下文：共享筛选条 + 下钻状态，所有小组件据此联动。
 */
export function provideStatsContext(options: { appId?: Ref<number | null> } = {}): StatsContext {
  const store = useStatsStore()
  const theme = useChartTheme()
  const drill = ref<DrillState>({ appId: null, day: null })
  const fixedAppId = computed(() => options.appId?.value ?? null)

  const data = computed(() => store.data)
  const scoped = computed(() => filterSessions(store.data.sessions, {
    steamIds: store.steamIds,
    includeApps: fixedAppId.value == null ? store.includeApps : [],
    excludeApps: fixedAppId.value == null ? store.excludeApps : [],
    appId: fixedAppId.value,
  }, store.timeOptions))
  const range = computed(() => store.range)
  const base = computed(() => {
    const [start, end] = rangeOfDays(range.value.from, range.value.to, store.timeOptions)
    return scoped.value.filter(s => s.start >= start && s.start <= end)
  })
  // 按天下钻不受日期范围限制（年度热力图可点击范围外的日期）
  const sessions = computed(() => {
    const { appId, day } = drill.value
    if (day) {
      return filterSessions(scoped.value, { appId, day }, store.timeOptions)
    }
    return appId == null ? base.value : filterSessions(base.value, { appId }, store.timeOptions)
  })

  // 同一视图内同一游戏颜色一致：按（范围内）总时长排名分配色板
  const colorMap = computed(() => {
    const totals = new Map<number, number>()
    for (const s of base.value) {
      totals.set(s.appId, (totals.get(s.appId) ?? 0) + s.duration)
    }
    for (const s of scoped.value) {
      if (!totals.has(s.appId)) {
        totals.set(s.appId, 0)
      }
    }
    const ids = [...totals.entries()].sort((a, b) => b[1] - a[1]).map(([id]) => id)
    return createColorMap(ids, theme.value.palette(Math.min(Math.max(ids.length, 1), 24)))
  })

  const context: StatsContext = {
    data,
    scoped,
    base,
    sessions,
    range,
    drill,
    fixedAppId,
    opts: computed(() => store.timeOptions),
    theme,
    topN: toRef(store, 'topN'),
    fmt: store.fmt,
    appName: appId => resolveAppName(store.data.apps, appId),
    colorOf: appId => colorMap.value.get(appId) ?? theme.value.palette(1)[0],
    setDrillApp: (appId) => {
      drill.value = { ...drill.value, appId: drill.value.appId === appId ? null : appId }
    },
    setDrillDay: (day) => {
      drill.value = { ...drill.value, day: drill.value.day === day ? null : day }
    },
    clearDrill: () => {
      drill.value = { appId: null, day: null }
    },
  }
  provide(STATS_CONTEXT_KEY, context)
  return context
}

export function useStatsContext(): StatsContext {
  const context = inject(STATS_CONTEXT_KEY)
  if (!context) {
    throw new Error('Stats context is not provided')
  }
  return context
}
