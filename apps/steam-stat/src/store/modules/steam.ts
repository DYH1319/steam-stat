import type { RendererIpcError } from '@/composables/useIpc'
import { normalizeIpcError, useIpc } from '@/composables/useIpc'

type BootstrapStatus = 'idle' | 'loading' | 'success' | 'error'

export const useSteamStore = defineStore('steam', () => {
  const ipc = useIpc()

  const loggedInAccounts = ref<string[]>([])
  const selectedAccountName = ref<string | null>(null)
  const operationalStatus = ref<SteamOperationalStatus | null>(null)
  const bootstrapStatus = ref<BootstrapStatus>('idle')
  const lastBootstrapAt = ref<number | null>(null)
  const bootstrapError = ref<RendererIpcError | null>(null)

  let generation = 0
  let bootstrapPromise: Promise<void> | null = null

  function reconcileSelection(): void {
    if (!selectedAccountName.value || !loggedInAccounts.value.includes(selectedAccountName.value)) {
      selectedAccountName.value = loggedInAccounts.value[0] ?? null
    }
  }

  async function refreshAccounts(): Promise<void> {
    const expectedGeneration = generation
    const accounts = await ipc.steamLoginLoggedInUsersGet()
    if (expectedGeneration !== generation) {
      return
    }
    loggedInAccounts.value = [...new Set(accounts)]
    reconcileSelection()
  }

  async function refreshOperationalStatus(): Promise<void> {
    const expectedGeneration = generation
    const status = await ipc.steamOperationalStatusGet()
    if (expectedGeneration !== generation) {
      return
    }
    operationalStatus.value = status
  }

  function ensureBootstrapped(): Promise<void> {
    if (bootstrapStatus.value === 'success') {
      return Promise.resolve()
    }
    if (bootstrapPromise) {
      return bootstrapPromise
    }
    const expectedGeneration = generation
    bootstrapStatus.value = 'loading'
    bootstrapError.value = null
    const pending = (async () => {
      try {
        await Promise.all([refreshAccounts(), refreshOperationalStatus()])
        if (expectedGeneration !== generation) {
          return
        }
        bootstrapStatus.value = 'success'
        lastBootstrapAt.value = Date.now()
      }
      catch (cause) {
        if (expectedGeneration === generation) {
          bootstrapError.value = normalizeIpcError(cause)
          bootstrapStatus.value = 'error'
        }
        throw cause
      }
    })()
    bootstrapPromise = pending
    const release = () => {
      if (bootstrapPromise === pending) {
        bootstrapPromise = null
      }
    }
    void pending.then(release, release)
    return pending
  }

  function selectAccount(accountName: string | null): void {
    if (accountName === null) {
      selectedAccountName.value = null
      return
    }
    if (loggedInAccounts.value.includes(accountName)) {
      selectedAccountName.value = accountName
    }
  }

  function addAccount(accountName: string): void {
    const trimmed = accountName.trim()
    if (!trimmed) {
      return
    }
    if (!loggedInAccounts.value.includes(trimmed)) {
      loggedInAccounts.value = [...loggedInAccounts.value, trimmed]
    }
    reconcileSelection()
  }

  function removeAccount(accountName: string): void {
    loggedInAccounts.value = loggedInAccounts.value.filter(name => name !== accountName)
    reconcileSelection()
  }

  function reset(): void {
    generation += 1
    bootstrapPromise = null
    loggedInAccounts.value = []
    selectedAccountName.value = null
    operationalStatus.value = null
    bootstrapStatus.value = 'idle'
    lastBootstrapAt.value = null
    bootstrapError.value = null
  }

  return {
    loggedInAccounts,
    selectedAccountName,
    operationalStatus,
    bootstrapStatus,
    lastBootstrapAt,
    bootstrapError,
    ensureBootstrapped,
    refreshAccounts,
    refreshOperationalStatus,
    selectAccount,
    addAccount,
    removeAccount,
    reset,
  }
})
