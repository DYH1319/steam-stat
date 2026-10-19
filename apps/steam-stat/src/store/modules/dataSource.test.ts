import type { Mock } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h } from 'vue'
import { ipcApiKey } from '@/composables/useIpc'
import { useDataSourceStore } from './dataSource'

interface DataSourceApiFakes {
  steamRefreshStatus: Mock<ElectronAPI['steamRefreshStatus']>
  steamOperationalStatusGet: Mock<ElectronAPI['steamOperationalStatusGet']>
  steamLocalDataSourcesGet: Mock<ElectronAPI['steamLocalDataSourcesGet']>
  steamGetLibraryFolders: Mock<ElectronAPI['steamGetLibraryFolders']>
}

function createFakes(): DataSourceApiFakes {
  return {
    steamRefreshStatus: vi.fn<ElectronAPI['steamRefreshStatus']>().mockResolvedValue(null),
    steamOperationalStatusGet: vi.fn<ElectronAPI['steamOperationalStatusGet']>()
      .mockResolvedValue({
        changedAt: 0,
        connectivity: 'online',
        dependencies: [],
        reauthenticationAccounts: [],
        resources: [],
        sessions: [],
      }),
    steamLocalDataSourcesGet: vi.fn<ElectronAPI['steamLocalDataSourcesGet']>()
      .mockResolvedValue({ checkedAt: 0, sources: [] }),
    steamGetLibraryFolders: vi.fn<ElectronAPI['steamGetLibraryFolders']>().mockResolvedValue([]),
  }
}

function createStore(fakes: DataSourceApiFakes) {
  const app = createApp(defineComponent({ render: () => h('div') }))
  const pinia = createPinia()
  app.use(pinia)
  app.provide(ipcApiKey, fakes as unknown as ElectronAPI)
  setActivePinia(pinia)
  app.mount(document.createElement('div'))
  return useDataSourceStore(pinia)
}

function withOperationalStatus(
  fakes: DataSourceApiFakes,
  sessions: SteamSessionStatus['state'][],
  connectivity: SteamOperationalStatus['connectivity'] = 'online',
) {
  fakes.steamOperationalStatusGet.mockResolvedValue({
    changedAt: 0,
    connectivity,
    dependencies: [],
    reauthenticationAccounts: [],
    resources: [],
    sessions: sessions.map(state => ({
      accountName: 'alice',
      generation: 1,
      reconnectAttempt: 0,
      state,
    })),
  })
}

describe('useDataSourceStore', () => {
  it('statusOf returns unknown before any load and follows local source status afterwards', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)

    expect(store.statusOf('file.loginUsersVdf')).toBe('unknown')
    expect(store.statusOf('cm.session')).toBe('unknown')
    expect(store.statusOf('webApi.http')).toBe('unknown')

    fakes.steamLocalDataSourcesGet.mockResolvedValue({
      checkedAt: 1,
      sources: [
        { id: 'file.loginUsersVdf', kind: 'file', status: 'ok' },
        { id: 'registry.steam', kind: 'registry', status: 'missing' },
        { id: 'file.configVdf', kind: 'file', status: 'parseFailed' },
      ],
    })
    await store.refreshLocalSources()

    expect(store.statusOf('file.loginUsersVdf')).toBe('ok')
    expect(store.statusOf('registry.steam')).toBe('missing')
    expect(store.statusOf('file.configVdf')).toBe('parseFailed')
    expect(store.statusOf('file.appManifests')).toBe('unknown')
  })

  it('statusOf maps cm.session to session activity', async () => {
    const fakes = createFakes()
    withOperationalStatus(fakes, ['ready', 'disconnected'])
    const store = createStore(fakes)
    await store.refreshOperationalStatus()
    expect(store.statusOf('cm.session')).toBe('ok')

    withOperationalStatus(fakes, ['disconnected', 'failed', 'stopping', 'reauthenticationRequired'])
    await store.refreshOperationalStatus()
    expect(store.statusOf('cm.session')).toBe('missing')

    withOperationalStatus(fakes, [])
    await store.refreshOperationalStatus()
    expect(store.statusOf('cm.session')).toBe('missing')
  })

  it('statusOf maps webApi.http to connectivity', async () => {
    const fakes = createFakes()
    withOperationalStatus(fakes, [], 'degraded')
    const store = createStore(fakes)
    await store.refreshOperationalStatus()
    expect(store.statusOf('webApi.http')).toBe('ok')

    withOperationalStatus(fakes, [], 'offline')
    await store.refreshOperationalStatus()
    expect(store.statusOf('webApi.http')).toBe('missing')
  })

  it('missingFor ignores unknown statuses', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)
    // 未加载任何数据：全部为 unknown，不阻断
    expect(store.missingFor(['file.loginUsersVdf', 'cm.session', 'webApi.http'])).toEqual([])

    fakes.steamLocalDataSourcesGet.mockResolvedValue({
      checkedAt: 1,
      sources: [{ id: 'file.loginUsersVdf', kind: 'file', status: 'missing' }],
    })
    withOperationalStatus(fakes, ['ready'], 'offline')
    await store.refreshLocalSources()
    await store.refreshOperationalStatus()

    expect(store.missingFor(['file.loginUsersVdf', 'cm.session', 'webApi.http', 'registry.steam']))
      .toEqual(['file.loginUsersVdf', 'webApi.http'])
  })

  it('pagePathFor routes ids to their data-source page', () => {
    const store = createStore(createFakes())
    expect(store.pagePathFor('cm.session')).toBe('/data-source/cm')
    expect(store.pagePathFor('webApi.http')).toBe('/data-source/web-api')
    expect(store.pagePathFor('file.loginUsersVdf')).toBe('/data-source/local')
    expect(store.pagePathFor('job.appRunningStatus')).toBe('/data-source/local')
  })

  it('ensureLoaded is memoized and resolves even when IPC fails', async () => {
    const fakes = createFakes()
    fakes.steamRefreshStatus.mockRejectedValue(new Error('ipc down'))
    const store = createStore(fakes)

    await Promise.all([store.ensureLoaded(), store.ensureLoaded()])
    expect(fakes.steamRefreshStatus).toHaveBeenCalledTimes(1)
    expect(fakes.steamOperationalStatusGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamLocalDataSourcesGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamGetLibraryFolders).toHaveBeenCalledTimes(1)

    await store.ensureLoaded()
    expect(fakes.steamRefreshStatus).toHaveBeenCalledTimes(1)
  })

  it('setPollInterval clamps to 5-600 seconds', () => {
    const store = createStore(createFakes())
    store.setPollInterval(1)
    expect(store.pollIntervalSeconds).toBe(5)
    store.setPollInterval(10000)
    expect(store.pollIntervalSeconds).toBe(600)
    store.setPollInterval(30)
    expect(store.pollIntervalSeconds).toBe(30)
  })

  it('refreshAll does not overlap while in flight', async () => {
    const fakes = createFakes()
    let resolveStatus!: (value: null) => void
    fakes.steamRefreshStatus.mockReturnValue(new Promise(resolve => (resolveStatus = resolve)))
    const store = createStore(fakes)

    const first = store.refreshAll()
    const second = store.refreshAll()
    // pinia action 包装器会返回新 Promise，无法断言引用相等；以 IPC 未重复调用证明复用同一轮刷新
    expect(fakes.steamRefreshStatus).toHaveBeenCalledTimes(1)
    expect(fakes.steamOperationalStatusGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamLocalDataSourcesGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamGetLibraryFolders).toHaveBeenCalledTimes(1)

    resolveStatus(null)
    await Promise.all([first, second])
    await store.refreshAll()
    expect(fakes.steamRefreshStatus).toHaveBeenCalledTimes(2)
  })
})
