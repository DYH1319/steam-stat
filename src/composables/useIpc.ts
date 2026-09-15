import type { InjectionKey } from 'vue'
import { hasInjectionContext } from 'vue'

export class RendererIpcError extends Error {
  readonly cause: unknown

  constructor(message: string, cause?: unknown) {
    super(message)
    this.name = 'RendererIpcError'
    this.cause = cause
  }
}

export const ipcApiKey: InjectionKey<ElectronAPI> = Symbol('electron-ipc')

export function provideIpc(api: ElectronAPI): void {
  provide(ipcApiKey, api)
}

export function normalizeIpcError(error: unknown): RendererIpcError {
  if (error instanceof RendererIpcError) {
    return error
  }
  if (error instanceof Error) {
    return new RendererIpcError(error.message || 'Electron IPC call failed', error.cause ?? error)
  }
  if (typeof error === 'string') {
    return new RendererIpcError(error)
  }
  return new RendererIpcError('Electron IPC call failed', error)
}

export function useIpc(): ElectronAPI {
  const provided = hasInjectionContext() ? inject(ipcApiKey, undefined) : undefined
  const api = provided ?? (typeof window === 'undefined' ? undefined : window.electron)
  if (!api) {
    throw new RendererIpcError('Electron IPC is unavailable: no injected api and window.electron is missing')
  }
  return new Proxy(api, {
    get(target, property, receiver) {
      const value = Reflect.get(target, property, receiver)
      if (typeof value !== 'function') {
        return value
      }
      return (...args: unknown[]) => {
        try {
          const result = Reflect.apply(value, target, args)
          return result instanceof Promise
            ? result.catch((error: unknown) => {
                throw normalizeIpcError(error)
              })
            : result
        }
        catch (error) {
          throw normalizeIpcError(error)
        }
      }
    },
  })
}

export function useIpcListener<TArgs extends unknown[]>(
  register: (callback: (...args: TArgs) => void) => void,
  remove: () => void,
  callback: (...args: TArgs) => void,
): void {
  onMounted(() => {
    register(callback)
  })
  onUnmounted(() => {
    remove()
  })
}
