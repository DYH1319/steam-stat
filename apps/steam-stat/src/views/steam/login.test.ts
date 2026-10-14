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
import LoginPage from './login.vue'

interface LoginApiFakes {
  steamLoginEventOnListener: Mock<ElectronAPI['steamLoginEventOnListener']>
  steamLoginEventRemoveListener: Mock<ElectronAPI['steamLoginEventRemoveListener']>
  steamLoginLoggedInUsersGet: Mock<ElectronAPI['steamLoginLoggedInUsersGet']>
  steamLoginSavedTokensGet: Mock<ElectronAPI['steamLoginSavedTokensGet']>
  steamOperationalStatusGet: Mock<ElectronAPI['steamOperationalStatusGet']>
}

const FaPageMainStub = defineComponent({
  name: 'FaPageMain',
  setup: (_props, { slots }) => () => h('div', slots.default?.()),
})

function createFakes(): LoginApiFakes {
  return {
    steamLoginEventOnListener: vi.fn<ElectronAPI['steamLoginEventOnListener']>(),
    steamLoginEventRemoveListener: vi.fn<ElectronAPI['steamLoginEventRemoveListener']>(),
    steamLoginLoggedInUsersGet: vi.fn<ElectronAPI['steamLoginLoggedInUsersGet']>()
      .mockResolvedValue(['alice']),
    steamLoginSavedTokensGet: vi.fn<ElectronAPI['steamLoginSavedTokensGet']>()
      .mockResolvedValue([]),
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

function mountPage(fakes: LoginApiFakes): { wrapper: VueWrapper, store: ReturnType<typeof useSteamStore> } {
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
  const wrapper = mount(LoginPage, {
    global: {
      plugins: [pinia, i18n, install],
    },
  })
  cleanupCallbacks.push(() => wrapper.unmount())
  return { wrapper, store }
}

function emitLoginEvent(fakes: LoginApiFakes, event: SteamLoginEvent) {
  const listener = fakes.steamLoginEventOnListener.mock.calls[0]?.[0]
  if (!listener) {
    throw new Error('login event listener was not registered')
  }
  listener(event)
}

describe('login page', () => {
  afterEach(() => {
    while (cleanupCallbacks.length > 0) {
      cleanupCallbacks.pop()!()
    }
  })

  it('registers the login listener on mount and removes it on unmount', async () => {
    const fakes = createFakes()
    const { wrapper } = mountPage(fakes)
    await flushPromises()

    expect(fakes.steamLoginEventOnListener).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    expect(fakes.steamLoginEventRemoveListener).toHaveBeenCalledTimes(1)
  })

  it('adds the account locally on success without refetching users', async () => {
    const fakes = createFakes()
    const { store } = mountPage(fakes)
    await flushPromises()

    emitLoginEvent(fakes, { type: 'success', data: { accountName: 'bob' } })
    await flushPromises()

    expect(store.loggedInAccounts).toEqual(['alice', 'bob'])
    expect(fakes.steamLoginLoggedInUsersGet).not.toHaveBeenCalled()
  })

  it('removes the account locally on disconnect without refetching users', async () => {
    const fakes = createFakes()
    const { store } = mountPage(fakes)
    await flushPromises()

    emitLoginEvent(fakes, { type: 'userDisconnected', data: { accountName: 'alice' } })
    await flushPromises()

    expect(store.loggedInAccounts).toEqual([])
    expect(store.selectedAccountName).toBeNull()
    expect(fakes.steamLoginLoggedInUsersGet).not.toHaveBeenCalled()
  })
})
