import type { VueWrapper } from '@vue/test-utils'
import type { App } from 'vue'
import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import {
  ipcApiKey,
  normalizeIpcError,
  provideIpc,
  RendererIpcError,
  useIpc,
  useIpcListener,
} from './useIpc'

function mountSetup(setup: () => void, api?: ElectronAPI): VueWrapper {
  const component = defineComponent({
    setup,
    render: () => h('div'),
  })
  if (!api) {
    return mount(component)
  }
  const installApi = (app: App) => {
    app.provide(ipcApiKey, api)
  }
  return mount(component, {
    global: {
      plugins: [installApi],
    },
  })
}

describe('normalizeIpcError', () => {
  it('returns an existing RendererIpcError unchanged', () => {
    const error = new RendererIpcError('typed', 'cause-value')
    expect(normalizeIpcError(error)).toBe(error)
  })

  it('keeps message and cause of native errors', () => {
    const cause = { code: 42 }
    const normalized = normalizeIpcError(new Error('boom', { cause }))
    expect(normalized).toBeInstanceOf(RendererIpcError)
    expect(normalized.message).toBe('boom')
    expect(normalized.cause).toBe(cause)
  })

  it('uses a string rejection as message', () => {
    const normalized = normalizeIpcError('plain failure')
    expect(normalized).toBeInstanceOf(RendererIpcError)
    expect(normalized.message).toBe('plain failure')
  })

  it('falls back to a generic message for other values', () => {
    const normalized = normalizeIpcError({ unexpected: true })
    expect(normalized).toBeInstanceOf(RendererIpcError)
    expect(normalized.message).not.toBe('')
    expect(normalized.cause).toEqual({ unexpected: true })
  })
})

describe('useIpc', () => {
  let hadElectron = false
  let originalElectron: ElectronAPI | undefined

  afterEach(() => {
    if (hadElectron && originalElectron) {
      window.electron = originalElectron
    }
    else {
      Reflect.deleteProperty(window, 'electron')
    }
  })

  function stubWindowElectron(api: ElectronAPI) {
    hadElectron = 'electron' in window
    originalElectron = window.electron
    window.electron = api
  }

  it('prefers the provided api over window.electron', () => {
    const providedGet = vi.fn<ElectronAPI['steamGetStatus']>().mockResolvedValue(null)
    const windowGet = vi.fn<ElectronAPI['steamGetStatus']>().mockResolvedValue(null)
    stubWindowElectron({ steamGetStatus: windowGet } as unknown as ElectronAPI)
    mountSetup(() => {
      void useIpc().steamGetStatus()
    }, { steamGetStatus: providedGet } as unknown as ElectronAPI)
    expect(providedGet).toHaveBeenCalledTimes(1)
    expect(windowGet).not.toHaveBeenCalled()
  })

  it('provides the api to descendants through provideIpc', () => {
    const providedGet = vi.fn<ElectronAPI['steamGetStatus']>().mockResolvedValue(null)
    let resolved: ElectronAPI | undefined
    const child = defineComponent({
      setup: () => {
        resolved = useIpc()
        return () => h('div')
      },
    })
    const parent = defineComponent({
      setup: () => {
        provideIpc({ steamGetStatus: providedGet } as unknown as ElectronAPI)
        return () => h(child)
      },
    })
    mount(parent)
    expect(resolved).toBeDefined()
    void resolved!.steamGetStatus()
    expect(providedGet).toHaveBeenCalledTimes(1)
  })

  it('falls back to window.electron when nothing is provided', () => {
    const windowGet = vi.fn<ElectronAPI['steamGetStatus']>().mockResolvedValue(null)
    stubWindowElectron({ steamGetStatus: windowGet } as unknown as ElectronAPI)
    let resolved: ElectronAPI | undefined
    mountSetup(() => {
      resolved = useIpc()
    })
    void resolved!.steamGetStatus()
    expect(windowGet).toHaveBeenCalledTimes(1)
  })

  it('returns context-bridge methods without violating non-configurable proxy invariants', async () => {
    const loggedInUsersGet = vi.fn<ElectronAPI['steamLoginLoggedInUsersGet']>().mockResolvedValue(['alice'])
    const api = {} as ElectronAPI
    Object.defineProperty(api, 'steamLoginLoggedInUsersGet', {
      value: loggedInUsersGet,
      enumerable: true,
      configurable: false,
      writable: false,
    })
    stubWindowElectron(api)

    let resolved: ElectronAPI | undefined
    mountSetup(() => {
      resolved = useIpc()
    })
    expect(resolved!.steamLoginLoggedInUsersGet).toBe(resolved!.steamLoginLoggedInUsersGet)
    await expect(resolved!.steamLoginLoggedInUsersGet()).resolves.toEqual(['alice'])
    expect(loggedInUsersGet).toHaveBeenCalledTimes(1)
  })

  it('throws a clear RendererIpcError when ipc is unavailable', () => {
    let caught: unknown
    mountSetup(() => {
      try {
        useIpc()
      }
      catch (error) {
        caught = error
      }
    })
    expect(caught).toBeInstanceOf(RendererIpcError)
    expect((caught as RendererIpcError).message).toMatch(/unavailable/i)
  })

  it('normalizes synchronous throws from ipc methods', () => {
    let caught: unknown
    mountSetup(() => {
      const ipc = useIpc()
      try {
        void ipc.steamGetStatus()
      }
      catch (error) {
        caught = error
      }
    }, {
      steamGetStatus: (() => {
        throw new Error('sync boom')
      }) as unknown as ElectronAPI['steamGetStatus'],
    } as unknown as ElectronAPI)
    expect(caught).toBeInstanceOf(RendererIpcError)
    expect((caught as RendererIpcError).message).toBe('sync boom')
  })

  it('normalizes promise rejections from ipc methods', async () => {
    let ipc: ElectronAPI | undefined
    const stringFailure: unknown = 'string failure'
    const weirdFailure: unknown = { weird: true }
    mountSetup(() => {
      ipc = useIpc()
    }, {
      steamGetStatus: (() => Promise.reject(new Error('async boom'))) as unknown as ElectronAPI['steamGetStatus'],
      steamGetLoginUser: (() => Promise.reject(stringFailure)) as unknown as ElectronAPI['steamGetLoginUser'],
      steamRefreshStatus: (() => Promise.reject(weirdFailure)) as unknown as ElectronAPI['steamRefreshStatus'],
    } as unknown as ElectronAPI)
    await expect(ipc!.steamGetStatus()).rejects.toBeInstanceOf(RendererIpcError)
    await expect(ipc!.steamGetStatus()).rejects.toMatchObject({ message: 'async boom' })
    await expect(ipc!.steamGetLoginUser()).rejects.toMatchObject({ message: 'string failure' })
    const weird = await ipc!.steamRefreshStatus().then(
      () => null,
      (error: unknown) => error,
    )
    expect(weird).toBeInstanceOf(RendererIpcError)
  })

  it('returns the same wrapped function for repeated provided method reads', () => {
    let ipc: ElectronAPI | undefined
    mountSetup(() => {
      ipc = useIpc()
    }, {
      steamGetStatus: vi.fn<ElectronAPI['steamGetStatus']>().mockResolvedValue(null),
    } as unknown as ElectronAPI)
    expect(ipc!.steamGetStatus).toBe(ipc!.steamGetStatus)
  })

  it('handles non-configurable provided methods', async () => {
    const getStatus = vi.fn<ElectronAPI['steamGetStatus']>().mockResolvedValue(null)
    const api = {} as ElectronAPI
    Object.defineProperty(api, 'steamGetStatus', {
      value: getStatus,
      enumerable: true,
      configurable: false,
      writable: false,
    })
    let ipc: ElectronAPI | undefined
    mountSetup(() => {
      ipc = useIpc()
    }, api)
    expect(ipc!.steamGetStatus).toBe(ipc!.steamGetStatus)
    await expect(ipc!.steamGetStatus()).resolves.toBeNull()
    expect(getStatus).toHaveBeenCalledTimes(1)
  })

  it('returns non-function properties unchanged', () => {
    let ipc: ElectronAPI | undefined
    mountSetup(() => {
      ipc = useIpc()
    }, {
      steamGetStatus: vi.fn(),
      customField: 'raw-value',
    } as unknown as ElectronAPI)
    expect((ipc as unknown as Record<string, unknown>).customField).toBe('raw-value')
  })
})

describe('useIpcListener', () => {
  it('registers the callback on mount and removes it on unmount', () => {
    const register = vi.fn<(callback: (value: number) => void) => void>()
    const remove = vi.fn<() => void>()
    const callback = vi.fn<(value: number) => void>()
    const wrapper = mountSetup(() => {
      useIpcListener<[number]>(register, remove, callback)
    })
    expect(register).toHaveBeenCalledTimes(1)
    expect(register).toHaveBeenCalledWith(callback)
    expect(remove).not.toHaveBeenCalled()
    wrapper.unmount()
    expect(remove).toHaveBeenCalledTimes(1)
  })

  it('passes event payloads to the callback', () => {
    let handler: ((value: number) => void) | undefined
    const register: (callback: (value: number) => void) => void = (callback) => {
      handler = callback
    }
    const callback = vi.fn<(value: number) => void>()
    mountSetup(() => {
      useIpcListener<[number]>(register, () => {}, callback)
    })
    handler!(7)
    expect(callback).toHaveBeenCalledWith(7)
  })
})
