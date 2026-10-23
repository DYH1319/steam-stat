import type { DataSourceId } from '@/types/dataSource'
import { useIpc } from '@/composables/useIpc'
import i18n from '@/i18n'

const t = i18n.global.t

type DataSourceSection = 'global' | 'operational' | 'local' | 'libraryFolders'

const DEFAULT_POLL_INTERVAL_SECONDS = 30
const MIN_POLL_INTERVAL_SECONDS = 5
const MAX_POLL_INTERVAL_SECONDS = 600
const FIRST_LOAD_TIMEOUT_MS = 5000

const SOURCE_LABEL_KEYS: Record<DataSourceId, string> = {
  'registry.steam': 'registrySteam',
  'registry.activeProcess': 'registryActiveProcess',
  'registry.apps': 'registryApps',
  'file.loginUsersVdf': 'loginUsersVdf',
  'file.libraryFoldersVdf': 'libraryFoldersVdf',
  'file.appManifests': 'appManifests',
  'file.configVdf': 'configVdf',
  'file.localConfigVdf': 'localConfigVdf',
  'job.appRunningStatus': 'appRunningStatusJob',
  'cm.session': 'cmSession',
}

const CM_SESSION_INACTIVE_STATES = ['disconnected', 'failed', 'stopping', 'reauthenticationRequired']

/**
 * 数据源健康状态：本地文件 / 注册表 / 后台任务 / CM 会话。
 * 全局轮询保持数据新鲜，路由守卫与菜单据此判断是否阻断或置灰。
 */
export const useDataSourceStore = defineStore('dataSource', () => {
  const ipc = useIpc()

  const globalStatus = ref<GlobalStatus | null>(null)
  const operationalStatus = ref<SteamOperationalStatus | null>(null)
  const localSources = ref<SteamLocalDataSources | null>(null)
  const libraryFolders = ref<string[]>([])

  const loading = ref<Record<DataSourceSection, boolean>>({
    global: false,
    operational: false,
    local: false,
    libraryFolders: false,
  })
  const lastRefreshedAt = ref<Record<DataSourceSection, number | null>>({
    global: null,
    operational: null,
    local: null,
    libraryFolders: null,
  })

  const pollIntervalSeconds = ref(DEFAULT_POLL_INTERVAL_SECONDS)

  let refreshPromise: Promise<PromiseSettledResult<void>[]> | null = null
  let loadPromise: Promise<void> | null = null
  let pollTimer: ReturnType<typeof setInterval> | null = null
  let hasEverLoaded = false

  async function refreshGlobalStatus(): Promise<void> {
    loading.value.global = true
    try {
      globalStatus.value = await ipc.steamRefreshStatus()
      lastRefreshedAt.value.global = Date.now()
    }
    finally {
      loading.value.global = false
    }
  }

  async function refreshOperationalStatus(): Promise<void> {
    loading.value.operational = true
    try {
      operationalStatus.value = await ipc.steamOperationalStatusGet()
      lastRefreshedAt.value.operational = Date.now()
    }
    finally {
      loading.value.operational = false
    }
  }

  async function refreshLocalSources(): Promise<void> {
    loading.value.local = true
    try {
      localSources.value = await ipc.steamLocalDataSourcesGet()
      lastRefreshedAt.value.local = Date.now()
    }
    finally {
      loading.value.local = false
    }
  }

  async function refreshLibraryFolders(): Promise<void> {
    loading.value.libraryFolders = true
    try {
      libraryFolders.value = await ipc.steamGetLibraryFolders()
      lastRefreshedAt.value.libraryFolders = Date.now()
    }
    finally {
      loading.value.libraryFolders = false
    }
  }

  function refreshAll(): Promise<PromiseSettledResult<void>[]> {
    if (!refreshPromise) {
      refreshPromise = Promise.allSettled([
        refreshGlobalStatus(),
        refreshOperationalStatus(),
        refreshLocalSources(),
        refreshLibraryFolders(),
      ]).finally(() => {
        hasEverLoaded = true
        refreshPromise = null
      })
    }
    return refreshPromise
  }

  function ensureLoaded(): Promise<void> {
    if (!loadPromise) {
      loadPromise = Promise.race([
        refreshAll().then(() => undefined),
        new Promise<void>(resolve => setTimeout(resolve, FIRST_LOAD_TIMEOUT_MS)),
      ])
    }
    return loadPromise
  }

  function schedulePoll() {
    if (pollTimer) {
      clearInterval(pollTimer)
    }
    pollTimer = setInterval(() => {
      if (typeof document !== 'undefined' && document.hidden) {
        return
      }
      void refreshAll()
    }, pollIntervalSeconds.value * 1000)
  }

  function startPolling(): void {
    if (pollTimer) {
      return
    }
    if (!hasEverLoaded) {
      void refreshAll()
    }
    schedulePoll()
  }

  function stopPolling(): void {
    if (pollTimer) {
      clearInterval(pollTimer)
      pollTimer = null
    }
  }

  function setPollInterval(seconds: number): void {
    const value = Math.min(MAX_POLL_INTERVAL_SECONDS, Math.max(MIN_POLL_INTERVAL_SECONDS, Math.round(seconds)))
    if (value === pollIntervalSeconds.value) {
      return
    }
    pollIntervalSeconds.value = value
    if (pollTimer) {
      schedulePoll()
    }
  }

  function statusOf(id: DataSourceId): 'ok' | 'missing' | 'parseFailed' | 'unknown' {
    if (id === 'cm.session') {
      if (!operationalStatus.value) {
        return 'unknown'
      }
      return operationalStatus.value.sessions.some(
        session => !CM_SESSION_INACTIVE_STATES.includes(session.state),
      )
        ? 'ok'
        : 'missing'
    }
    const source = localSources.value?.sources.find(item => item.id === id)
    return source?.status ?? 'unknown'
  }

  function missingFor(ids: DataSourceId[] = []): DataSourceId[] {
    return ids.filter(id => statusOf(id) === 'missing' || statusOf(id) === 'parseFailed')
  }

  function pagePathFor(id: DataSourceId): string {
    if (id.startsWith('cm.')) {
      return '/data-source/cm'
    }
    return '/data-source/local'
  }

  function labelOf(id: DataSourceId): string {
    return t(`dataSource.source.${SOURCE_LABEL_KEYS[id]}`)
  }

  return {
    globalStatus,
    operationalStatus,
    localSources,
    libraryFolders,
    loading,
    lastRefreshedAt,
    pollIntervalSeconds,
    refreshGlobalStatus,
    refreshOperationalStatus,
    refreshLocalSources,
    refreshLibraryFolders,
    refreshAll,
    ensureLoaded,
    startPolling,
    stopPolling,
    setPollInterval,
    statusOf,
    missingFor,
    pagePathFor,
    labelOf,
  }
}, {
  persist: {
    pick: ['pollIntervalSeconds'],
  },
})
