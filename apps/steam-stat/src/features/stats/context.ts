import type { InjectionKey, Ref } from 'vue'
import type { SessionInput, SessionPiece, StatsPrefs } from './index'
import { storeToRefs } from 'pinia'
import { computed, inject, provide, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useStatsStore } from '@/store/modules/stats'
import { clipSessions, logicalDayStart, nextDayKey, totalsByApp } from './index'

export interface StatsContextOptions {
  /** 固定单 app 场景（如游戏详情页复用） */
  fixedAppId?: Ref<number | undefined>
  prefs: Ref<StatsPrefs>
  palette: Ref<string[]>
  /** 是否应用日期范围过滤（默认 true） */
  applyDateRange?: boolean
  /** 是否应用 include/exclude app 过滤（默认 true） */
  applyIncludeExclude?: boolean
  /** 使用本地 drill 而非全局 store drill */
  localDrill?: boolean
}

export interface StatsContext {
  allSessions: Ref<SessionInput[]>
  scoped: Ref<SessionInput[]>
  sessions: Ref<SessionPiece[]>
  rangeScoped: Ref<SessionPiece[]>
  drilled: Ref<SessionPiece[]>
  totals: Ref<ReturnType<typeof totalsByApp>>
  appMeta: Ref<Map<number, Record<string, string | undefined>>>
  appName: (appId: number) => string
  appColor: (appId: number) => string
  preInstallByApp: Ref<Map<number, number>>
  users: Ref<SteamUser[]>
  loading: Ref<boolean>
  lastUpdateTime: Ref<number>
  drill: Ref<{ day?: string, appId?: number }>
  palette: Ref<string[]>
  refresh: () => Promise<void>
}

const key: InjectionKey<StatsContext> = Symbol('stats-context')

/** 统计页共享数据上下文：一次拉取全量，过滤在客户端完成 */
export function createStatsContext(options: StatsContextOptions): StatsContext {
  const electronApi = (window as Window).electron
  const locale = useI18n().locale
  const statsStore = useStatsStore()

  const records = ref<UseAppRecord[]>([])
  const users = ref<SteamUser[]>([])
  const appMeta = ref(new Map<number, Record<string, string | undefined>>())
  const appNames = ref(new Map<number, { name?: string, localized?: string }>())
  const preInstallByApp = ref(new Map<number, number>())
  const loading = ref(false)
  const lastUpdateTime = ref(0)
  const palette = options.palette

  const localDrill = ref<{ day?: string, appId?: number }>({})
  const { drill: storeDrill } = storeToRefs(statsStore)
  const drill = options.localDrill ? localDrill : storeDrill

  const applyIncludeExclude = options.applyIncludeExclude !== false
  const applyDateRange = options.applyDateRange !== false

  const allSessions = computed<SessionInput[]>(() =>
    records.value.filter(r =>
      (options.fixedAppId?.value == null || r.appId === options.fixedAppId.value)
      && (!applyIncludeExclude
        || ((statsStore.filters.includeAppIds.length === 0 || statsStore.filters.includeAppIds.includes(r.appId))
          && !statsStore.filters.excludeAppIds.includes(r.appId))),
    ),
  )

  const scoped = computed(() =>
    drill.value.appId == null
      ? allSessions.value
      : allSessions.value.filter(s => s.appId === drill.value.appId),
  )

  const sessions = computed(() => {
    const range = statsStore.filters.dateRange
    if (!applyDateRange || !range) {
      return clipSessions(allSessions.value, 0, Number.MAX_SAFE_INTEGER)
    }
    const from = logicalDayStart(range[0], options.prefs.value)
    const to = logicalDayStart(nextDayKey(range[1]), options.prefs.value)
    return clipSessions(allSessions.value, from, to)
  })

  const rangeScoped = computed(() =>
    drill.value.appId == null
      ? sessions.value
      : sessions.value.filter(p => p.appId === drill.value.appId),
  )

  const drilled = computed(() => {
    if (!drill.value.day) {
      return rangeScoped.value
    }
    const start = logicalDayStart(drill.value.day, options.prefs.value)
    const end = logicalDayStart(nextDayKey(drill.value.day), options.prefs.value)
    return clipSessions(rangeScoped.value.map(p => p.session), start, end)
  })

  // 颜色排名基于全量 totals，保证 drill/日期变化时同一游戏颜色稳定
  const allTotals = computed(() => totalsByApp(clipSessions(allSessions.value, 0, Number.MAX_SAFE_INTEGER)))
  const totals = computed(() => totalsByApp(drilled.value))

  function appName(appId: number): string {
    const meta = appNames.value.get(appId)
    if (meta?.localized) {
      try {
        const map = JSON.parse(meta.localized) as Record<string, string>
        const localized = map[locale.value] ?? map['zh-CN'] ?? map['en-US']
        if (localized) {
          return localized
        }
      }
      catch {
        // 回退原名
      }
    }
    return meta?.name || `App ${appId}`
  }

  function appColor(appId: number): string {
    const rank = allTotals.value.findIndex(t => t.appId === appId)
    if (palette.value.length === 0) {
      return '#999999'
    }
    return palette.value[(rank < 0 ? 0 : rank) % palette.value.length]
  }

  async function refresh() {
    loading.value = true
    try {
      const steamIds = statsStore.filters.steamIds.length ? statsStore.filters.steamIds : null
      const recordsResult = await electronApi.steamGetValidUseAppRecord({ steamIds })
      records.value = recordsResult.records
      users.value = await electronApi.steamGetUsersInRecord()
      const apps = await electronApi.steamGetAppsInfo({})
      const meta = new Map<number, Record<string, string | undefined>>()
      const names = new Map<number, { name?: string, localized?: string }>()
      for (const app of apps) {
        meta.set(app.appId, { developer: app.developer ?? undefined, publisher: app.publisher ?? undefined, type: app.type ?? undefined })
        names.set(app.appId, { name: app.name ?? undefined, localized: app.nameLocalized })
      }
      appMeta.value = meta
      appNames.value = names
      try {
        const reconcile = await electronApi.historyReconcileGet()
        const map = new Map<number, number>()
        for (const item of reconcile.items) {
          map.set(item.appId, (map.get(item.appId) ?? 0) + item.preTrackingSeconds)
        }
        preInstallByApp.value = map
      }
      catch {
        // 历史对账不可用时忽略
      }
      lastUpdateTime.value = Date.now()
    }
    finally {
      loading.value = false
    }
  }

  const context: StatsContext = {
    allSessions,
    scoped,
    sessions,
    rangeScoped,
    drilled,
    totals,
    appMeta,
    appName,
    appColor,
    preInstallByApp,
    users,
    loading,
    lastUpdateTime,
    drill,
    palette,
    refresh,
  }
  return context
}

export function provideStatsContext(context: StatsContext) {
  provide(key, context)
}

export function useStatsContext(): StatsContext {
  const context = inject(key)
  if (!context) {
    throw new Error('StatsContext is not provided')
  }
  return context
}
