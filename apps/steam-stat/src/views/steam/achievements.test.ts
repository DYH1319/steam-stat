import type { DOMWrapper, VueWrapper } from '@vue/test-utils'
import type { Mock } from 'vitest'
import type { App } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h } from 'vue'
import { ipcApiKey } from '@/composables/useIpc'
import i18n from '@/i18n'
import { useSteamStore } from '@/store/modules/steam'
import AchievementsPage from './achievements.vue'

interface AchievementApiFakes {
  steamAchievementsGameGet: Mock<ElectronAPI['steamAchievementsGameGet']>
  steamAchievementsGameRefresh: Mock<ElectronAPI['steamAchievementsGameRefresh']>
  steamAchievementsOverviewGet: Mock<ElectronAPI['steamAchievementsOverviewGet']>
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

function makeOverviewItem(
  appId: number,
  name: string,
  percentage: number,
): SteamAchievementOverviewItem {
  return {
    appId,
    lastPlayedAt: 1_700_000_000,
    localizedName: name,
    name,
    playtimeForever: 120,
    progress: {
      appId,
      percentage,
      total: 20,
      unlocked: Math.round((percentage / 100) * 20),
    },
  }
}

function makeOverviewResult(
  games: SteamAchievementOverviewItem[],
): SteamAchievementOverviewResult {
  return {
    accountName: 'alice',
    games,
    partial: false,
    status: 'success',
  }
}

function makeAchievement(internalName: string, displayName: string): SteamAchievement {
  return {
    archived: false,
    hidden: false,
    icon: 'https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/1/icon.jpg',
    iconGray: 'https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/1/icon_gray.jpg',
    internalName,
    isRevealed: true,
    isUnlocked: true,
    localizedDescription: `${displayName} description`,
    localizedName: displayName,
    progressType: 'none',
    unlockTimeUtc: 1_700_000_000,
  }
}

function makeGameResult(
  appId: number,
  appName: string,
  achievementName: string,
): SteamAchievementGameResult {
  return {
    achievements: [makeAchievement(`ach-${appId}-1`, achievementName)],
    appId,
    appName,
    groups: [],
    language: 'english',
    partial: false,
    progressState: { freshness: 'fresh', hasValue: true, source: 'cm' },
    stale: false,
    status: 'success',
    summary: { total: 1, unlocked: 1, unknown: 0 },
  }
}

function makeFailureGameResult(appId: number): SteamAchievementGameResult {
  return {
    achievements: [],
    appId,
    appName: '',
    diagnosticCode: 'steam-offline',
    failure: 'offline',
    groups: [],
    language: 'english',
    partial: false,
    progressState: { failure: 'offline', hasValue: false },
    stale: false,
    status: 'failure',
    summary: { total: 0, unlocked: 0, unknown: 0 },
  }
}

function createFakes(games: SteamAchievementOverviewItem[]): AchievementApiFakes {
  return {
    steamAchievementsGameGet: vi.fn<ElectronAPI['steamAchievementsGameGet']>(),
    steamAchievementsGameRefresh: vi.fn<ElectronAPI['steamAchievementsGameRefresh']>(),
    steamAchievementsOverviewGet: vi.fn<ElectronAPI['steamAchievementsOverviewGet']>()
      .mockResolvedValue(makeOverviewResult(games)),
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

function mountPage(fakes: AchievementApiFakes): { wrapper: VueWrapper, store: ReturnType<typeof useSteamStore> } {
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
  const wrapper = mount(AchievementsPage, {
    global: {
      plugins: [pinia, i18n, install],
    },
  })
  cleanupCallbacks.push(() => wrapper.unmount())
  return { wrapper, store }
}

function overviewRow(wrapper: VueWrapper, text: string): DOMWrapper<Element> {
  const row = wrapper.findAll('[data-testid="overview-row"]')
    .find(candidate => candidate.text().includes(text))
  if (!row) {
    throw new Error(`overview row containing "${text}" not found`)
  }
  return row
}

describe('achievements page', () => {
  const originalClientHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientHeight')

  beforeEach(() => {
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get: () => 560,
    })
  })

  afterEach(() => {
    while (cleanupCallbacks.length > 0) {
      cleanupCallbacks.pop()!()
    }
    if (originalClientHeight) {
      Object.defineProperty(HTMLElement.prototype, 'clientHeight', originalClientHeight)
    }
    else {
      Reflect.deleteProperty(HTMLElement.prototype, 'clientHeight')
    }
  })

  it('loads only the virtualized overview for a pre-bootstrapped account', async () => {
    const games = Array.from({ length: 1001 }, (_, index) =>
      makeOverviewItem(index + 1, `Game ${index + 1}`, (index * 7) % 100))
    const fakes = createFakes(games)
    fakes.steamAchievementsGameGet.mockResolvedValue(makeGameResult(1, 'First detail', 'First achievement'))

    const { wrapper } = mountPage(fakes)
    await flushPromises()

    expect(fakes.steamAchievementsOverviewGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamAchievementsGameGet).not.toHaveBeenCalled()
    expect(fakes.steamAchievementsGameRefresh).not.toHaveBeenCalled()

    const rows = wrapper.findAll('[data-testid="overview-row"]')
    expect(rows.length).toBeGreaterThan(0)
    expect(rows.length).toBeLessThan(1001)

    await rows[0].trigger('click')
    await flushPromises()
    expect(fakes.steamAchievementsGameGet).toHaveBeenCalledTimes(1)
    expect(fakes.steamAchievementsGameRefresh).not.toHaveBeenCalled()
  })

  it('keeps the old detail and fires a single forced refresh on failure', async () => {
    const fakes = createFakes([makeOverviewItem(1, 'Game One', 50)])
    fakes.steamAchievementsGameGet.mockResolvedValue(makeGameResult(1, 'Old detail', 'Old achievement'))
    const refresh = deferred<SteamAchievementGameResult>()
    fakes.steamAchievementsGameRefresh.mockReturnValue(refresh.promise)

    const { wrapper } = mountPage(fakes)
    await flushPromises()
    await overviewRow(wrapper, 'Game One').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Old detail')
    expect(wrapper.text()).toContain('Old achievement')

    await wrapper.find('[data-testid="refresh-detail"]').trigger('click')
    await wrapper.find('[data-testid="refresh-detail"]').trigger('click')
    await flushPromises()
    expect(fakes.steamAchievementsGameRefresh).toHaveBeenCalledTimes(1)

    refresh.resolve(makeFailureGameResult(1))
    await flushPromises()

    expect(wrapper.text()).toContain('Old detail')
    expect(wrapper.text()).toContain('Old achievement')
    expect(wrapper.find('.ant-alert-warning').exists()).toBe(true)
    expect(fakes.steamAchievementsGameRefresh).toHaveBeenCalledTimes(1)
  })

  it('clears the previous game detail while the next game is loading', async () => {
    const fakes = createFakes([
      makeOverviewItem(1, 'Game One', 60),
      makeOverviewItem(2, 'Game Two', 40),
    ])
    const secondGame = deferred<SteamAchievementGameResult>()
    fakes.steamAchievementsGameGet.mockImplementation((request) => {
      return request.appId === 1
        ? Promise.resolve(makeGameResult(1, 'Old detail', 'Old achievement'))
        : secondGame.promise
    })

    const { wrapper } = mountPage(fakes)
    await flushPromises()
    await overviewRow(wrapper, 'Game One').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Old detail')
    expect(wrapper.text()).toContain('Old achievement')

    await overviewRow(wrapper, 'Game Two').trigger('click')
    await flushPromises()
    expect(fakes.steamAchievementsGameGet).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('Old detail')
    expect(wrapper.text()).not.toContain('Old achievement')

    secondGame.resolve(makeGameResult(2, 'New detail', 'New achievement'))
    await flushPromises()
    expect(wrapper.text()).toContain('New detail')
    expect(wrapper.text()).toContain('New achievement')
    expect(wrapper.text()).not.toContain('Old detail')
    expect(wrapper.text()).not.toContain('Old achievement')
  })
})
