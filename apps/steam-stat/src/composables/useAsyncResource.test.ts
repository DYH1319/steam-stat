import type { AsyncResource } from './useAsyncResource'
import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { useAsyncResource } from './useAsyncResource'
import { RendererIpcError } from './useIpc'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useAsyncResource', () => {
  it('starts idle and resolves the initial request', async () => {
    const loader = vi.fn<(id: number) => Promise<string>>().mockResolvedValue('ok')
    const resource = useAsyncResource(loader)
    expect(resource.status.value).toBe('idle')
    expect(resource.data.value).toBeUndefined()

    const pending = resource.execute(1)
    expect(resource.status.value).toBe('loading')
    expect(resource.isInitialLoading.value).toBe(true)
    expect(loader).toHaveBeenCalledWith(1)

    await expect(pending).resolves.toBe('ok')
    expect(resource.data.value).toBe('ok')
    expect(resource.status.value).toBe('success')
    expect(resource.isInitialLoading.value).toBe(false)
    expect(resource.hasData.value).toBe(true)
    expect(resource.error.value).toBeUndefined()
  })

  it('reports a normalized error when the initial request fails', async () => {
    const loader = vi.fn<() => Promise<string>>().mockRejectedValue(new Error('first failure'))
    const resource = useAsyncResource(loader)
    await expect(resource.execute()).resolves.toBeUndefined()
    expect(resource.status.value).toBe('error')
    expect(resource.error.value).toBeInstanceOf(RendererIpcError)
    expect(resource.error.value?.message).toBe('first failure')
    expect(resource.data.value).toBeUndefined()
  })

  it('keeps previous data when a refresh fails', async () => {
    const loader = vi.fn<() => Promise<string>>()
      .mockResolvedValueOnce('cached')
      .mockRejectedValueOnce(new Error('refresh failure'))
    const resource = useAsyncResource(loader)
    await resource.execute()

    const pending = resource.refresh()
    expect(resource.status.value).toBe('refreshing')
    expect(resource.isRefreshing.value).toBe(true)
    await expect(pending).resolves.toBeUndefined()
    expect(resource.data.value).toBe('cached')
    expect(resource.status.value).toBe('error')
    expect(resource.error.value).toBeInstanceOf(RendererIpcError)
  })

  it('retries with the last arguments', async () => {
    const loader = vi.fn<(id: number) => Promise<string>>()
      .mockRejectedValueOnce(new Error('nope'))
      .mockResolvedValueOnce('recovered')
    const resource = useAsyncResource(loader)
    await resource.execute(5)
    await resource.retry()
    expect(loader).toHaveBeenLastCalledWith(5)
    expect(resource.data.value).toBe('recovered')
    expect(resource.status.value).toBe('success')
    expect(resource.error.value).toBeUndefined()
  })

  it('lets the latest request win when an older request resolves late', async () => {
    const first = deferred<string>()
    const second = deferred<string>()
    const loader = vi.fn<(id: number) => Promise<string>>()
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise)
    const resource = useAsyncResource(loader)
    const pendingFirst = resource.execute(1)
    const pendingSecond = resource.execute(2)

    second.resolve('new')
    await expect(pendingSecond).resolves.toBe('new')
    expect(resource.data.value).toBe('new')

    first.resolve('old')
    await expect(pendingFirst).resolves.toBeUndefined()
    expect(resource.data.value).toBe('new')
    expect(resource.status.value).toBe('success')
  })

  it('lets the latest request win when an older request rejects late', async () => {
    const first = deferred<string>()
    const second = deferred<string>()
    const loader = vi.fn<(id: number) => Promise<string>>()
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise)
    const resource = useAsyncResource(loader)
    const pendingFirst = resource.execute(1)
    const pendingSecond = resource.execute(2)

    second.resolve('new')
    await pendingSecond

    first.reject(new Error('late failure'))
    await expect(pendingFirst).resolves.toBeUndefined()
    expect(resource.data.value).toBe('new')
    expect(resource.status.value).toBe('success')
    expect(resource.error.value).toBeUndefined()
  })

  it('reuses the in-flight request for refresh and retry', async () => {
    const work = deferred<string>()
    const loader = vi.fn<(id: number) => Promise<string>>().mockReturnValue(work.promise)
    const resource = useAsyncResource(loader)
    const pending = resource.execute(1)
    const refresh = resource.refresh()
    const retry = resource.retry()
    expect(refresh).toBe(pending)
    expect(retry).toBe(pending)
    expect(loader).toHaveBeenCalledTimes(1)
    work.resolve('done')
    await pending
    expect(resource.data.value).toBe('done')
  })

  it('resolves undefined when refresh or retry run before any execute', async () => {
    const loader = vi.fn<() => Promise<string>>()
    const resource = useAsyncResource(loader)
    await expect(resource.refresh()).resolves.toBeUndefined()
    await expect(resource.retry()).resolves.toBeUndefined()
    expect(loader).not.toHaveBeenCalled()
    expect(resource.status.value).toBe('idle')
  })

  it('invalidates late results after reset', async () => {
    const work = deferred<string>()
    const loader = vi.fn<(id: number) => Promise<string>>().mockReturnValue(work.promise)
    const resource = useAsyncResource(loader)
    const pending = resource.execute(1)
    resource.reset()
    expect(resource.status.value).toBe('idle')
    expect(resource.data.value).toBeUndefined()
    expect(resource.error.value).toBeUndefined()

    work.resolve('late')
    await pending
    expect(resource.data.value).toBeUndefined()
    expect(resource.status.value).toBe('idle')
  })

  it('does not commit late results after the scope is disposed', async () => {
    const work = deferred<string>()
    const loader = vi.fn<(id: number) => Promise<string>>().mockReturnValue(work.promise)
    let resource!: AsyncResource<string, [number]>
    const wrapper = mount(defineComponent({
      setup: () => {
        resource = useAsyncResource(loader)
        return () => h('div')
      },
    }))
    const pending = resource.execute(1)
    expect(resource.status.value).toBe('loading')
    wrapper.unmount()
    work.resolve('late')
    await pending
    expect(resource.data.value).toBeUndefined()
  })
})
