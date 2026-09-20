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
import FriendsPage from './friends.vue'

interface FriendsApiFakes {
  shellOpenExternal: Mock<ElectronAPI['shellOpenExternal']>
  steamFriendsRecordsClear: Mock<ElectronAPI['steamFriendsRecordsClear']>
  steamFriendsRecordsGet: Mock<ElectronAPI['steamFriendsRecordsGet']>
  steamFriendsRefresh: Mock<ElectronAPI['steamFriendsRefresh']>
  steamFriendsSnapshotGet: Mock<ElectronAPI['steamFriendsSnapshotGet']>
  steamFriendsTrackGet: Mock<ElectronAPI['steamFriendsTrackGet']>
  steamFriendsTrackStart: Mock<ElectronAPI['steamFriendsTrackStart']>
  steamFriendsTrackStop: Mock<ElectronAPI['steamFriendsTrackStop']>
  steamFriendsUpdateOnListener: Mock<ElectronAPI['steamFriendsUpdateOnListener']>
  steamFriendsUpdateRemoveListener: Mock<ElectronAPI['steamFriendsUpdateRemoveListener']>
  steamLoginLoggedInUsersGet: Mock<ElectronAPI['steamLoginLoggedInUsersGet']>
  steamOperationalStatusGet: Mock<ElectronAPI['steamOperationalStatusGet']>
}

const FaPageMainStub = defineComponent({
  name: 'FaPageMain',
  setup: (_props, { slots }) => () => h('div', slots.default?.()),
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

function makeFriend(steamId: string, personaName: string): SteamFriendInfo {
  return {
    avatarHash: '',
    gameId: '0',
    gameName: '',
    lastLogOff: 0,
    lastLogOn: 0,
    personaName,
    personaState: 1,
    personaStateFlags: 0,
    relationship: 3,
    richPresence: '',
    steamId,
  }
}

function makeFriendData(
  accountName: string,
  lastUpdateTime: number,
  personaName: string,
): SteamFriendData {
  return {
    accountName,
    currentUser: makeFriend('76561198000000000', personaName),
    friends: [makeFriend('76561198000000001', `${personaName} Friend`)],
    lastUpdateTime,
  }
}

function makeResult(
  accounts: SteamFriendData[],
  status: SteamFriendsResult['status'] = 'success',
  resources: SteamResourceStatus[] = [],
): SteamFriendsResult {
  return { accounts, resources, status }
}

function createFakes(): FriendsApiFakes {
  return {
    shellOpenExternal: vi.fn<ElectronAPI['shellOpenExternal']>(),
    steamFriendsRecordsClear: vi.fn<ElectronAPI['steamFriendsRecordsClear']>().mockResolvedValue(0),
    steamFriendsRecordsGet: vi.fn<ElectronAPI['steamFriendsRecordsGet']>().mockResolvedValue([]),
    steamFriendsRefresh: vi.fn<ElectronAPI['steamFriendsRefresh']>(),
    steamFriendsSnapshotGet: vi.fn<ElectronAPI['steamFriendsSnapshotGet']>()
      .mockResolvedValue(makeResult([makeFriendData('alice', 1_700_000_000, 'Snapshot Alice')])),
    steamFriendsTrackGet: vi.fn<ElectronAPI['steamFriendsTrackGet']>().mockResolvedValue([]),
    steamFriendsTrackStart: vi.fn<ElectronAPI['steamFriendsTrackStart']>().mockResolvedValue(true),
    steamFriendsTrackStop: vi.fn<ElectronAPI['steamFriendsTrackStop']>().mockResolvedValue(true),
    steamFriendsUpdateOnListener: vi.fn<ElectronAPI['steamFriendsUpdateOnListener']>(),
    steamFriendsUpdateRemoveListener: vi.fn<ElectronAPI['steamFriendsUpdateRemoveListener']>(),
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

function mountPage(fakes: FriendsApiFakes): { wrapper: VueWrapper, store: ReturnType<typeof useSteamStore> } {
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
  const wrapper = mount(FriendsPage, {
    global: {
      plugins: [pinia, i18n, install],
    },
  })
  cleanupCallbacks.push(() => wrapper.unmount())
  return { wrapper, store }
}

function emitFriendsUpdate(fakes: FriendsApiFakes, event: SteamFriendsUpdateEvent) {
  const listener = fakes.steamFriendsUpdateOnListener.mock.calls[0]?.[0]
  if (!listener) {
    throw new Error('friends update listener was not registered')
  }
  listener(event)
}

describe('friends page', () => {
  afterEach(() => {
    while (cleanupCallbacks.length > 0) {
      cleanupCallbacks.pop()!()
    }
  })

  it('registers the update listener on mount and removes it on unmount', async () => {
    const fakes = createFakes()
    const { wrapper } = mountPage(fakes)
    await flushPromises()

    expect(fakes.steamFriendsUpdateOnListener).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    expect(fakes.steamFriendsUpdateRemoveListener).toHaveBeenCalledTimes(1)
  })

  it('keeps a newer event when a late older snapshot resolves', async () => {
    const fakes = createFakes()
    const snapshot = deferred<SteamFriendsResult>()
    fakes.steamFriendsSnapshotGet.mockReturnValue(snapshot.promise)
    const { wrapper } = mountPage(fakes)
    await flushPromises()

    emitFriendsUpdate(fakes, {
      accountName: 'alice',
      data: makeFriendData('alice', 2_000, 'Event Alice'),
    })
    await flushPromises()
    expect(wrapper.text()).toContain('Event Alice')

    snapshot.resolve(makeResult([makeFriendData('alice', 1_000, 'Snapshot Alice')]))
    await flushPromises()

    expect(wrapper.text()).toContain('Event Alice')
    expect(wrapper.text()).not.toContain('Snapshot Alice')
    expect(fakes.steamFriendsSnapshotGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamFriendsRefresh).not.toHaveBeenCalled()
  })

  it('retains old friend data when refresh rejects', async () => {
    const fakes = createFakes()
    const { wrapper } = mountPage(fakes)
    await flushPromises()
    expect(wrapper.text()).toContain('Snapshot Alice')
    expect(fakes.steamFriendsSnapshotGet).toHaveBeenCalledTimes(1)

    fakes.steamFriendsRefresh.mockRejectedValue(new Error('steam offline'))
    await wrapper.find('[data-testid="refresh-friends"]').trigger('click')
    await flushPromises()

    expect(fakes.steamFriendsRefresh).toHaveBeenCalledTimes(1)
    expect(fakes.steamFriendsSnapshotGet).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Snapshot Alice')
    expect(wrapper.find('.ant-alert').exists()).toBe(true)

    fakes.steamFriendsRefresh.mockResolvedValue(
      makeResult([makeFriendData('alice', 1_800_000_000, 'Refreshed Alice')]),
    )
    await wrapper.find('[data-testid="retry-friends"]').trigger('click')
    await flushPromises()

    expect(fakes.steamFriendsRefresh).toHaveBeenCalledTimes(2)
    expect(fakes.steamFriendsSnapshotGet).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Refreshed Alice')
  })
})
