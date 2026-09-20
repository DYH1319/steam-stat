import type { VueWrapper } from '@vue/test-utils'
import type { Mock } from 'vitest'
import type { App } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h } from 'vue'
import { ipcApiKey } from '@/composables/useIpc'
import i18n from '@/i18n'
import { useSteamStore } from '@/store/modules/steam'
import LibraryPage from './library.vue'

interface LibraryApiFakes {
  steamLibraryRefresh: Mock<ElectronAPI['steamLibraryRefresh']>
  steamLibrarySnapshotGet: Mock<ElectronAPI['steamLibrarySnapshotGet']>
  steamLoginLoggedInUsersGet: Mock<ElectronAPI['steamLoginLoggedInUsersGet']>
  steamOperationalStatusGet: Mock<ElectronAPI['steamOperationalStatusGet']>
}

const FaPageMainStub = defineComponent({
  name: 'FaPageMain',
  setup: (_props, { slots }) => () => h('div', Object.values(slots).map(slot => slot?.())),
})

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function makeGame(appId: number, name: string): SteamOwnedGame {
  return {
    achievementPercentage: 0,
    achievementTotal: 0,
    achievementUnlocked: 0,
    appId,
    contentDescriptorIds: [],
    hasCommunityVisibleStats: false,
    imgIconUrl: '',
    isFamilyShared: false,
    isInWishlist: false,
    isOwned: true,
    name,
    nameLocalized: name,
    ownerNames: [],
    ownerSteamIds: [],
    playtime2Weeks: 0,
    playtimeForever: 120,
    rtimeLastPlayed: 1_700_000_000,
  }
}

function makeResult(
  libraries: Record<string, SteamOwnedGame[]>,
  status: SteamLibraryResult['status'] = 'success',
  resources: SteamResourceStatus[] = [],
): SteamLibraryResult {
  return { libraries, resources, status }
}

function createFakes(): LibraryApiFakes {
  return {
    steamLibraryRefresh: vi.fn<ElectronAPI['steamLibraryRefresh']>(),
    steamLibrarySnapshotGet: vi.fn<ElectronAPI['steamLibrarySnapshotGet']>()
      .mockResolvedValue(makeResult({ alice: [makeGame(1, 'Game One')] }, 'success', [
        {
          accountName: 'alice',
          freshness: 'fresh',
          lastSuccessfulUpdate: 1_700_000_000,
          resourceKind: 'library-snapshot',
          source: 'cm',
        },
      ])),
    steamLoginLoggedInUsersGet: vi.fn<ElectronAPI['steamLoginLoggedInUsersGet']>()
      .mockResolvedValue(['alice']),
    steamOperationalStatusGet: vi.fn<ElectronAPI['steamOperationalStatusGet']>()
      .mockResolvedValue({
        changedAt: 0,
        connectivity: 'online',
        dependencies: [],
        reauthenticationAccounts: [],
        resources: [],
        sessions: [],
      }),
  }
}

const cleanupCallbacks: Array<() => void> = []

function mountPage(fakes: LibraryApiFakes): { wrapper: VueWrapper, store: ReturnType<typeof useSteamStore> } {
  const bootstrapApp = createApp(defineComponent({ render: () => h('div') }))
  const pinia = createPinia()
  bootstrapApp.use(pinia)
  bootstrapApp.provide(ipcApiKey, fakes as unknown as ElectronAPI)
  setActivePinia(pinia)
  bootstrapApp.mount(document.createElement('div'))
  cleanupCallbacks.push(() => bootstrapApp.unmount())

  const store = useSteamStore(pinia)
  store.bootstrapStatus = 'success'
  store.loggedInAccounts = ['alice']
  store.selectedAccountName = 'alice'

  const install = (app: App) => {
    app.provide(ipcApiKey, fakes as unknown as ElectronAPI)
    app.component('FaPageMain', FaPageMainStub)
  }
  const wrapper = mount(LibraryPage, {
    global: {
      plugins: [pinia, i18n, install],
    },
  })
  cleanupCallbacks.push(() => wrapper.unmount())
  return { wrapper, store }
}

describe('library page', () => {
  afterEach(() => {
    while (cleanupCallbacks.length > 0) {
      cleanupCallbacks.pop()!()
    }
  })

  it('loads the snapshot exactly once on mount', async () => {
    const fakes = createFakes()
    const { wrapper } = mountPage(fakes)
    await flushPromises()

    expect(fakes.steamLibrarySnapshotGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamLibraryRefresh).not.toHaveBeenCalled()
    expect(fakes.steamLoginLoggedInUsersGet).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Game One')
  })

  it('runs exactly one refresh call without a second snapshot', async () => {
    const fakes = createFakes()
    fakes.steamLibraryRefresh.mockResolvedValue(
      makeResult({ alice: [makeGame(1, 'Game One'), makeGame(2, 'Game Two')] }),
    )
    const { wrapper } = mountPage(fakes)
    await flushPromises()

    await wrapper.find('[data-testid="sync-library"]').trigger('click')
    await flushPromises()

    expect(fakes.steamLibraryRefresh).toHaveBeenCalledTimes(1)
    expect(fakes.steamLibrarySnapshotGet).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Game Two')
  })

  it('keeps old games and retries through the refresh channel on rejection', async () => {
    const fakes = createFakes()
    const { wrapper } = mountPage(fakes)
    await flushPromises()
    expect(wrapper.text()).toContain('Game One')

    const rejected = deferred<SteamLibraryResult>()
    fakes.steamLibraryRefresh.mockReturnValue(rejected.promise)
    await wrapper.find('[data-testid="sync-library"]').trigger('click')
    rejected.reject(new Error('steam offline'))
    await flushPromises()

    expect(wrapper.text()).toContain('Game One')
    expect(wrapper.find('.ant-alert').exists()).toBe(true)
    expect(fakes.steamLibrarySnapshotGet).toHaveBeenCalledTimes(1)

    fakes.steamLibraryRefresh.mockResolvedValue(makeResult({ alice: [makeGame(3, 'Game Three')] }))
    await wrapper.find('[data-testid="retry-library"]').trigger('click')
    await flushPromises()

    expect(fakes.steamLibraryRefresh).toHaveBeenCalledTimes(2)
    expect(fakes.steamLibrarySnapshotGet).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Game Three')
  })
})
