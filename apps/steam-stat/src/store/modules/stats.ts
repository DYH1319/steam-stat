import type { StatsPrefs } from '@/features/stats'

export interface StatsDashboardWidget {
  key: string
  widgetId: string
  span: 1 | 2
}

export interface StatsDashboardLayout {
  id: string
  name: string
  widgets: StatsDashboardWidget[]
}

/** 统计页共享过滤/偏好/仪表盘布局（持久化）；drill 下钻状态不持久化 */
export const useStatsStore = defineStore('stats', () => {
  const filters = ref<{
    steamIds: string[]
    dateRange: [string, string] | null
    includeAppIds: number[]
    excludeAppIds: number[]
  }>({ steamIds: [], dateRange: null, includeAppIds: [], excludeAppIds: [] })

  const prefs = ref<StatsPrefs & { topN: number, granularity: 'hour' | 'day' | 'week' | 'month' }>({
    topN: 10,
    granularity: 'day',
    dayStartHour: 0,
    weekStartsOn: 1,
  })

  const dashboard = ref<{ layouts: StatsDashboardLayout[], activeLayoutId: string }>({
    layouts: [{
      id: 'default',
      name: '默认布局',
      widgets: [
        { key: 'w1', widgetId: 'summary', span: 2 },
        { key: 'w2', widgetId: 'durationShare', span: 1 },
        { key: 'w3', widgetId: 'timeTrend', span: 1 },
        { key: 'w4', widgetId: 'hourWeekHeatmap', span: 1 },
        { key: 'w5', widgetId: 'yearHeatmap', span: 2 },
        { key: 'w6', widgetId: 'launchFrequency', span: 1 },
      ],
    }],
    activeLayoutId: 'default',
  })

  const drill = ref<{ day?: string, appId?: number }>({})

  function clearDrill() {
    drill.value = {}
  }

  // 旧版 useRecord.vue 的过滤存储一次性迁移
  try {
    const legacy = localStorage.getItem('filters')
    if (legacy) {
      const parsed = JSON.parse(legacy) as { selectedUsers?: string[], selectedData?: [string, string] }
      if (parsed.selectedUsers?.length) {
        filters.value.steamIds = parsed.selectedUsers
      }
      if (Array.isArray(parsed.selectedData) && parsed.selectedData.length === 2) {
        filters.value.dateRange = parsed.selectedData
      }
      localStorage.removeItem('filters')
    }
  }
  catch {
    // 忽略损坏的迁移数据
  }

  return { filters, prefs, dashboard, drill, clearDrill }
}, {
  persist: {
    pick: ['filters', 'prefs', 'dashboard'],
  },
})
