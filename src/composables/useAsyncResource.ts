import type { ComputedRef, Ref } from 'vue'
import type { RendererIpcError } from './useIpc'
import { normalizeIpcError } from './useIpc'

export type AsyncResourceStatus = 'idle' | 'loading' | 'success' | 'refreshing' | 'error'

export interface AsyncResource<T, TArgs extends unknown[]> {
  data: Ref<T | undefined>
  error: Ref<RendererIpcError | undefined>
  status: Ref<AsyncResourceStatus>
  hasData: ComputedRef<boolean>
  isInitialLoading: ComputedRef<boolean>
  isRefreshing: ComputedRef<boolean>
  execute: (...args: TArgs) => Promise<T | undefined>
  refresh: () => Promise<T | undefined>
  retry: () => Promise<T | undefined>
  reset: () => void
}

export function useAsyncResource<T, TArgs extends unknown[]>(
  loader: (...args: TArgs) => Promise<T>,
): AsyncResource<T, TArgs> {
  const data = shallowRef<T | undefined>(undefined)
  const error = ref<RendererIpcError | undefined>(undefined)
  const status = ref<AsyncResourceStatus>('idle')

  let requestSeq = 0
  let active: Promise<T | undefined> | null = null
  let lastArgs: TArgs | null = null
  let disposed = false

  if (getCurrentScope()) {
    onScopeDispose(() => {
      disposed = true
    })
  }

  const hasData = computed(() => data.value !== undefined)
  const isInitialLoading = computed(() => status.value === 'loading')
  const isRefreshing = computed(() => status.value === 'refreshing')

  function start(args: TArgs): Promise<T | undefined> {
    const requestId = ++requestSeq
    status.value = hasData.value ? 'refreshing' : 'loading'
    let raw: Promise<T>
    try {
      raw = Promise.resolve(loader(...args))
    }
    catch (cause) {
      raw = Promise.reject(cause)
    }
    const promise = raw.then(
      (result) => {
        if (!disposed && requestId === requestSeq) {
          data.value = result
          error.value = undefined
          status.value = 'success'
          return result
        }
        return undefined
      },
      (cause: unknown) => {
        if (!disposed && requestId === requestSeq) {
          error.value = normalizeIpcError(cause)
          status.value = 'error'
        }
        return undefined
      },
    )
    active = promise
    void promise.then(() => {
      if (active === promise) {
        active = null
      }
    })
    return promise
  }

  function execute(...args: TArgs): Promise<T | undefined> {
    lastArgs = args
    return start(args)
  }

  function rerun(): Promise<T | undefined> {
    if (active) {
      return active
    }
    if (!lastArgs) {
      return Promise.resolve(undefined)
    }
    return start(lastArgs)
  }

  function reset(): void {
    requestSeq += 1
    active = null
    lastArgs = null
    data.value = undefined
    error.value = undefined
    status.value = 'idle'
  }

  return {
    data,
    error,
    status,
    hasData,
    isInitialLoading,
    isRefreshing,
    execute,
    refresh: rerun,
    retry: rerun,
    reset,
  }
}
