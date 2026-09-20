import type { Mock } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h } from 'vue'
import { ipcApiKey, RendererIpcError } from '@/composables/useIpc'
import { useSteamStore } from './steam'

interface SteamApiFakes {
  steamLoginLoggedInUsersGet: Mock<ElectronAPI['steamLoginLoggedInUsersGet']>
  steamOperationalStatusGet: Mock<ElectronAPI['steamOperationalStatusGet']>
}

function createFakes(): SteamApiFakes {
  return {
    steamLoginLoggedInUsersGet: vi.fn<ElectronAPI['steamLoginLoggedInUsersGet']>()
      .mockResolvedValue(['alice', 'bob']),
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

function createStore(fakes: SteamApiFakes) {
  const app = createApp(defineComponent({ render: () => h('div') }))
  const pinia = createPinia()
  app.use(pinia)
  app.provide(ipcApiKey, fakes as unknown as ElectronAPI)
  setActivePinia(pinia)
  app.mount(document.createElement('div'))
  return useSteamStore(pinia)
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useSteamStore', () => {
  it('shares a single bootstrap promise across concurrent callers', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)
    await Promise.all([store.ensureBootstrapped(), store.ensureBootstrapped()])
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamOperationalStatusGet).toHaveBeenCalledTimes(1)
    expect(store.bootstrapStatus).toBe('success')
    expect(store.loggedInAccounts).toEqual(['alice', 'bob'])
    expect(store.selectedAccountName).toBe('alice')
    expect(store.lastBootstrapAt).not.toBeNull()
  })

  it('does not bootstrap again after success', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)
    await store.ensureBootstrapped()
    await store.ensureBootstrapped()
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamOperationalStatusGet).toHaveBeenCalledTimes(1)
  })

  it('records a typed bootstrap error and allows retry', async () => {
    const fakes = createFakes()
    fakes.steamLoginLoggedInUsersGet.mockRejectedValueOnce(new Error('ipc down'))
    const store = createStore(fakes)
    await expect(store.ensureBootstrapped()).rejects.toBeInstanceOf(RendererIpcError)
    expect(store.bootstrapStatus).toBe('error')
    expect(store.bootstrapError).toBeInstanceOf(RendererIpcError)
    expect(store.bootstrapError?.message).toBe('ipc down')

    await store.ensureBootstrapped()
    expect(store.bootstrapStatus).toBe('success')
    expect(store.bootstrapError).toBeNull()
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(2)
  })

  it('deduplicates accounts while preserving order and fixes the selection', async () => {
    const fakes = createFakes()
    fakes.steamLoginLoggedInUsersGet.mockResolvedValue(['bob', 'alice', 'bob', 'alice'])
    const store = createStore(fakes)
    await store.refreshAccounts()
    expect(store.loggedInAccounts).toEqual(['bob', 'alice'])
    expect(store.selectedAccountName).toBe('bob')

    store.selectAccount('alice')
    expect(store.selectedAccountName).toBe('alice')

    fakes.steamLoginLoggedInUsersGet.mockResolvedValue(['bob'])
    await store.refreshAccounts()
    expect(store.selectedAccountName).toBe('bob')

    fakes.steamLoginLoggedInUsersGet.mockResolvedValue([])
    await store.refreshAccounts()
    expect(store.selectedAccountName).toBeNull()
  })

  it('only accepts known accounts or null in selectAccount', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)
    await store.refreshAccounts()
    store.selectAccount('mallory')
    expect(store.selectedAccountName).toBe('alice')
    store.selectAccount('bob')
    expect(store.selectedAccountName).toBe('bob')
    store.selectAccount(null)
    expect(store.selectedAccountName).toBeNull()
  })

  it('adds accounts locally without another logged-in-users ipc call', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)
    await store.ensureBootstrapped()
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(1)

    store.addAccount('  carol  ')
    expect(store.loggedInAccounts).toEqual(['alice', 'bob', 'carol'])
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(1)

    store.addAccount('alice')
    store.addAccount('   ')
    expect(store.loggedInAccounts).toEqual(['alice', 'bob', 'carol'])

    store.selectAccount('carol')
    store.addAccount('dave')
    expect(store.selectedAccountName).toBe('carol')
  })

  it('removes accounts locally and reconciles the selection', async () => {
    const fakes = createFakes()
    const store = createStore(fakes)
    await store.ensureBootstrapped()
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(1)

    store.selectAccount('bob')
    store.removeAccount('bob')
    expect(store.loggedInAccounts).toEqual(['alice'])
    expect(store.selectedAccountName).toBe('alice')
    expect(fakes.steamLoginLoggedInUsersGet).toHaveBeenCalledTimes(1)

    store.removeAccount('alice')
    store.removeAccount('mallory')
    expect(store.loggedInAccounts).toEqual([])
    expect(store.selectedAccountName).toBeNull()
  })

  it('prevents late bootstrap results from overwriting a reset', async () => {
    const fakes = createFakes()
    const accounts = deferred<string[]>()
    const status = deferred<SteamOperationalStatus>()
    fakes.steamLoginLoggedInUsersGet.mockReturnValue(accounts.promise)
    fakes.steamOperationalStatusGet.mockReturnValue(status.promise)
    const store = createStore(fakes)

    const bootstrap = store.ensureBootstrapped()
    expect(store.bootstrapStatus).toBe('loading')
    store.reset()
    expect(store.bootstrapStatus).toBe('idle')
    expect(store.loggedInAccounts).toEqual([])

    accounts.resolve(['late'])
    status.resolve({
      changedAt: 1,
      connectivity: 'offline',
      dependencies: [],
      reauthenticationAccounts: [],
      resources: [],
      sessions: [],
    })
    await bootstrap
    expect(store.loggedInAccounts).toEqual([])
    expect(store.selectedAccountName).toBeNull()
    expect(store.operationalStatus).toBeNull()
    expect(store.bootstrapStatus).toBe('idle')

    fakes.steamLoginLoggedInUsersGet.mockResolvedValue(['carol'])
    await store.ensureBootstrapped()
    expect(store.bootstrapStatus).toBe('success')
    expect(store.loggedInAccounts).toEqual(['carol'])
  })
})
