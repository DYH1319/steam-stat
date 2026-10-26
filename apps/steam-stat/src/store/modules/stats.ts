import type { DurationFormat } from '@/features/stats/format'
import type { StatsData } from '@/features/stats/model'
import type { Granularity, TimeOptions } from '@/features/stats/time'
import type { WidgetId } from '@/features/stats/widgets'
import { useIpc } from '@/composables/useIpc'
import { formatDuration } from '@/features/stats/format'
import { decodeDataset, emptyStatsData } from '@/features/stats/model'
import { DAY_FORMAT, dayKey, startOfWeek } from '@/features/stats/time'
import { DEFAULT_LAYOUT_WIDGETS } from '@/features/stats/widgets'
import i18n from '@/i18n'
import dayjs from '@/utils/dayjs'

export type RangePreset = '7d' | '14d' | '30d' | '90d' | '180d' | '365d' | 'thisWeek' | 'thisMonth' | 'thisYear' | 'lastYear' | 'all' | 'custom'

export const RANGE_PRESETS: RangePreset[] = ['7d', '14d', '30d', '90d', '180d', '365d', 'thisWeek', 'thisMonth', 'thisYear', 'lastYear', 'all']

export interface DashboardWidget {
  id: WidgetId
  span: 1 | 2
}

export interface DashboardLayout {
  id: string
  name: string
  widgets: DashboardWidget[]
}

const PAGE_SIZE = 8000

function defaultLayout(): DashboardLayout {
  return { id: 'default', name: '', widgets: DEFAULT_LAYOUT_WIDGETS.map(widget => ({ ...widget })) }
}

/**
 * 统计与可视化：显示偏好（分界点 / 周起始 / 时长格式 / TopN / 粒度）、共享筛选条、
 * 自定义仪表盘布局（持久化），以及统计数据集的加载与缓存（不持久化）。
 */
export const useStatsStore = defineStore('stats', () => {
  const ipc = useIpc()

  // 显示偏好
  const dayBoundaryHour = ref(4)
  const weekStart = ref<0 | 1>(1)
  const durationFormat = ref<DurationFormat>('hm')
  const topN = ref(10)
  const granularity = ref<Exclude<Granularity, 'hour'> | 'hour'>('day')

  const timeOptions = computed<TimeOptions>(() => ({ dayBoundaryHour: dayBoundaryHour.value, weekStart: weekStart.value }))

  function fmt(seconds: number): string {
    const t = i18n.global.t
    return formatDuration(seconds, durationFormat.value, {
      day: t('stats.unit.day'),
      hour: t('stats.unit.hour'),
      minute: t('stats.unit.minute'),
    })
  }

  // 共享筛选条
  const steamIds = ref<string[]>([])
  const rangePreset = ref<RangePreset>('30d')
  const customRange = ref<[string, string] | null>(null)
  const includeApps = ref<number[]>([])
  const excludeApps = ref<number[]>([])

  function resetFilters() {
    steamIds.value = []
    rangePreset.value = '30d'
    customRange.value = null
    includeApps.value = []
    excludeApps.value = []
  }

  // 仪表盘布局
  const layouts = ref<DashboardLayout[]>([defaultLayout()])
  const activeLayoutId = ref('default')
  const activeLayout = computed(() => layouts.value.find(layout => layout.id === activeLayoutId.value) ?? layouts.value[0])

  function addLayout(name: string, copyFrom?: DashboardLayout) {
    const layout: DashboardLayout = {
      id: `layout-${Date.now().toString(36)}`,
      name,
      widgets: (copyFrom?.widgets ?? DEFAULT_LAYOUT_WIDGETS).map(widget => ({ ...widget })),
    }
    layouts.value.push(layout)
    activeLayoutId.value = layout.id
  }

  function removeLayout(id: string) {
    if (layouts.value.length <= 1) {
      return
    }
    layouts.value = layouts.value.filter(layout => layout.id !== id)
    if (activeLayoutId.value === id) {
      activeLayoutId.value = layouts.value[0].id
    }
  }

  function resetLayout(id: string) {
    const layout = layouts.value.find(item => item.id === id)
    if (layout) {
      layout.widgets = DEFAULT_LAYOUT_WIDGETS.map(widget => ({ ...widget }))
    }
  }

  // 数据集
  const data = shallowRef<StatsData>(emptyStatsData())
  const loading = ref(false)
  const loadedAt = ref<number | null>(null)
  const error = ref<string | null>(null)
  const now = ref(Date.now())
  let loadPromise: Promise<void> | null = null

  async function fetchAll(): Promise<StatsData> {
    // 同一 channel 的并发 invoke 会互删回复监听（见 utils/appSettings.ts），分页请求严格串行
    const pages: StatsDataset[] = []
    let offset: number | null | undefined = 0
    while (offset != null) {
      const page: StatsDataset = await ipc.statsDatasetGet({ offset, limit: PAGE_SIZE })
      pages.push(page)
      offset = page.nextOffset
    }
    return decodeDataset(pages)
  }

  function reload(): Promise<void> {
    if (!loadPromise) {
      loading.value = true
      now.value = Date.now()
      loadPromise = fetchAll()
        .then((result) => {
          data.value = result
          loadedAt.value = Date.now()
          error.value = null
        })
        .catch((cause: unknown) => {
          error.value = cause instanceof Error ? cause.message : String(cause)
          throw cause
        })
        .finally(() => {
          loading.value = false
          loadPromise = null
        })
    }
    return loadPromise
  }

  function ensureLoaded(): Promise<void> {
    return loadedAt.value ? Promise.resolve() : reload()
  }

  const todayKey = computed(() => dayKey(dayjs(now.value).unix(), timeOptions.value))

  /** 当前筛选的日期范围（逻辑日 key，含两端） */
  const range = computed<{ from: string, to: string }>(() => {
    const today = dayjs(todayKey.value, DAY_FORMAT)
    const to = today.format(DAY_FORMAT)
    const days = (n: number) => ({ from: today.subtract(n - 1, 'day').format(DAY_FORMAT), to })
    switch (rangePreset.value) {
      case '7d': return days(7)
      case '14d': return days(14)
      case '30d': return days(30)
      case '90d': return days(90)
      case '180d': return days(180)
      case '365d': return days(365)
      case 'thisWeek': return { from: startOfWeek(today, timeOptions.value).format(DAY_FORMAT), to }
      case 'thisMonth': return { from: today.startOf('month').format(DAY_FORMAT), to }
      case 'thisYear': return { from: today.startOf('year').format(DAY_FORMAT), to }
      case 'lastYear': {
        const last = today.subtract(1, 'year')
        return { from: last.startOf('year').format(DAY_FORMAT), to: last.endOf('year').format(DAY_FORMAT) }
      }
      case 'custom':
        if (customRange.value) {
          return { from: customRange.value[0], to: customRange.value[1] }
        }
        return days(30)
      default: {
        const first = data.value.sessions[0]
        return { from: first ? dayKey(first.start, timeOptions.value) : to, to }
      }
    }
  })

  return {
    dayBoundaryHour,
    weekStart,
    durationFormat,
    topN,
    granularity,
    timeOptions,
    fmt,
    steamIds,
    rangePreset,
    customRange,
    includeApps,
    excludeApps,
    resetFilters,
    layouts,
    activeLayoutId,
    activeLayout,
    addLayout,
    removeLayout,
    resetLayout,
    data,
    loading,
    loadedAt,
    error,
    reload,
    ensureLoaded,
    todayKey,
    range,
  }
}, {
  persist: {
    pick: [
      'dayBoundaryHour',
      'weekStart',
      'durationFormat',
      'topN',
      'granularity',
      'steamIds',
      'rangePreset',
      'customRange',
      'includeApps',
      'excludeApps',
      'layouts',
      'activeLayoutId',
    ],
  },
})
