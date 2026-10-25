<script setup lang="ts">
import type { DashboardWidget } from '@/store/modules/stats'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useAsyncResource } from '@/composables/useAsyncResource'
import { useIpc } from '@/composables/useIpc'
import { byApp, totals } from '@/features/stats/aggregate'
import { provideStatsContext } from '@/features/stats/context'
import { steamHeaderImage, steamHeroImage } from '@/features/stats/model'
import { useStatsStore } from '@/store/modules/stats'
import { useSteamStore } from '@/store/modules/steam'
import dayjs from '@/utils/dayjs'
import StatsFilterBar from './stats/StatsFilterBar.vue'
import WidgetGrid from './stats/WidgetGrid.vue'

defineOptions({
  name: 'SteamGameDetail',
})

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const ipc = useIpc()
const store = useStatsStore()
const steamStore = useSteamStore()

const appId = computed(() => {
  const value = Number(route.params.appId)
  return Number.isInteger(value) && value > 0 ? value : null
})
const ctx = provideStatsContext({ appId })

const name = computed(() => appId.value ? ctx.appName(appId.value) : '')
const app = computed(() => appId.value ? ctx.data.value.apps.get(appId.value) : undefined)

const allTime = computed(() => totals(ctx.scoped.value, ctx.opts.value))
const aggregate = computed(() => byApp(ctx.scoped.value)[0])
const baselines = computed(() => {
  const steamIds = store.steamIds.length ? new Set(store.steamIds) : null
  return ctx.data.value.baselines.filter(b => b.appId === appId.value && (!steamIds || steamIds.has(b.steamId)))
})
const officialSeconds = computed(() => baselines.value.reduce((sum, b) => sum + b.officialMinutes * 60, 0))
const baselineSeconds = computed(() => baselines.value.reduce((sum, b) => sum + b.baselineSeconds, 0))
const untrackedSeconds = computed(() => baselines.value.reduce((sum, b) => sum + Math.max(0, b.untrackedSeconds), 0))

const stats = computed(() => [
  { key: 'local', value: store.fmt(allTime.value.seconds) },
  { key: 'official', value: officialSeconds.value ? store.fmt(officialSeconds.value) : '—' },
  { key: 'lifetime', value: store.fmt(allTime.value.seconds + baselineSeconds.value) },
  { key: 'sessions', value: String(allTime.value.count) },
  { key: 'firstPlayed', value: aggregate.value ? dayjs.unix(aggregate.value.first).format('YYYY-MM-DD') : '—' },
  { key: 'lastPlayed', value: aggregate.value ? dayjs.unix(aggregate.value.last).format('YYYY-MM-DD') : '—' },
])

const tab = ref('overview')
const tabs = computed(() => [
  { value: 'overview', label: t('game.tab.overview'), icon: 'i-mdi:view-dashboard-outline' },
  { value: 'sessions', label: t('game.tab.sessions'), icon: 'i-mdi:timeline-clock-outline' },
  { value: 'achievements', label: t('game.tab.achievements'), icon: 'i-mdi:trophy-variant' },
  { value: 'friends', label: t('game.tab.friends'), icon: 'i-mdi:account-group' },
])

const overviewWidgets: DashboardWidget[] = [
  { id: 'summary', span: 2 },
  { id: 'lifecycle', span: 1 },
  { id: 'hourWeekHeatmap', span: 1 },
  { id: 'timeTrend', span: 2 },
  { id: 'yearHeatmap', span: 2 },
  { id: 'personalRecords', span: 2 },
]
const sessionWidgets: DashboardWidget[] = [
  { id: 'recentSessions', span: 2 },
  { id: 'sessionGantt', span: 2 },
  { id: 'drillDetail', span: 2 },
]

// 成就：需要已登录的 Steam 账号
const achievements = useAsyncResource((accountName: string, id: number) =>
  ipc.steamAchievementsGameGet({ accountName, appId: id }))
const recentUnlocks = computed(() => (achievements.data.value?.achievements ?? [])
  .filter(a => a.isUnlocked && a.unlockTimeUtc)
  .sort((a, b) => (b.unlockTimeUtc ?? 0) - (a.unlockTimeUtc ?? 0))
  .slice(0, 12))

// 好友：当前正在玩该游戏的好友（来自好友快照）
const friends = useAsyncResource(() => ipc.steamFriendsSnapshotGet())
const playingFriends = computed(() => (friends.data.value?.accounts ?? [])
  .flatMap(account => account.friends)
  .filter(friend => friend.gameId === String(appId.value))
  .filter((friend, index, list) => list.findIndex(other => other.steamId === friend.steamId) === index))

let socialLoadedFor: number | null = null
async function loadSocial() {
  if (!appId.value || socialLoadedFor === appId.value) {
    return
  }
  socialLoadedFor = appId.value
  achievements.reset()
  try {
    await steamStore.ensureBootstrapped()
  }
  catch {
    return
  }
  if (steamStore.selectedAccountName) {
    await achievements.execute(steamStore.selectedAccountName, appId.value)
    await friends.execute()
  }
}

watch(tab, (value) => {
  if (value === 'achievements' || value === 'friends') {
    void loadSocial()
  }
})

watch(appId, () => {
  ctx.clearDrill()
  tab.value = 'overview'
  socialLoadedFor = null
})

const bannerFailed = ref(false)
watch(appId, () => {
  bannerFailed.value = false
})

function openExternal(url: string) {
  ipc.shellOpenExternal(url)
}

onMounted(() => {
  store.ensureLoaded().catch((e: any) => toast.error(`${t('common.getFailed')}: ${e?.message || e}`))
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <div v-if="!appId" class="text-muted-foreground py-16 text-center">
        {{ t('game.invalid') }}
      </div>
      <template v-else>
        <div class="banner mb-4 rounded-lg relative overflow-hidden">
          <img
            v-if="!bannerFailed"
            :src="steamHeroImage(appId)"
            class="banner-bg h-full w-full inset-0 absolute object-cover"
            referrerpolicy="no-referrer"
            alt=""
            @error="bannerFailed = true"
          >
          <div class="banner-mask inset-0 absolute" />
          <div class="p-5 flex flex-wrap gap-5 items-end relative">
            <img
              :src="steamHeaderImage(appId)"
              class="rounded-md w-56 aspect-[460/215] shadow-lg object-cover"
              referrerpolicy="no-referrer"
              :alt="name"
              @error="($event.target as HTMLImageElement).style.visibility = 'hidden'"
            >
            <div class="text-white flex-1 min-w-60">
              <div class="text-xs opacity-80">
                AppID {{ appId }}
                <template v-if="app?.developer">
                  · {{ app.developer }}
                </template>
                <template v-if="app?.type">
                  · {{ app.type }}
                </template>
              </div>
              <h2 class="text-3xl font-bold">
                {{ name }}
              </h2>
              <div class="mt-3 flex flex-wrap gap-x-6 gap-y-2">
                <div v-for="item in stats" :key="item.key">
                  <div class="text-xs opacity-75">
                    {{ t(`game.stat.${item.key}`) }}
                  </div>
                  <div class="text-lg font-semibold tabular-nums">
                    {{ item.value }}
                  </div>
                </div>
              </div>
              <div v-if="untrackedSeconds > 0" class="text-xs mt-2 opacity-80">
                {{ t('game.untracked', { value: store.fmt(untrackedSeconds) }) }}
              </div>
            </div>
            <div class="flex flex-wrap gap-2">
              <FaButton variant="secondary" @click="router.back()">
                <FaIcon name="i-mdi:arrow-left" />
                {{ t('game.back') }}
              </FaButton>
              <FaButton variant="secondary" @click="openExternal(`https://store.steampowered.com/app/${appId}`)">
                <FaIcon name="i-mdi:steam" />
                {{ t('game.store') }}
              </FaButton>
              <FaButton variant="secondary" @click="openExternal(`https://steamdb.info/app/${appId}/`)">
                <FaIcon name="i-mdi:database-search-outline" />
                SteamDB
              </FaButton>
            </div>
          </div>
        </div>

        <StatsFilterBar hide-apps />

        <FaTabs v-model="tab" :list="tabs" list-class="justify-start w-fit" content-class="pt-4">
          <template #overview>
            <WidgetGrid :widgets="overviewWidgets" />
          </template>
          <template #sessions>
            <WidgetGrid :widgets="sessionWidgets" />
          </template>
          <template #achievements>
            <div v-if="!steamStore.selectedAccountName && steamStore.bootstrapStatus !== 'loading'" class="text-sm text-muted-foreground py-12 text-center flex flex-col gap-3 items-center">
              <FaIcon name="i-mdi:account-lock-outline" class="opacity-50 size-12" />
              {{ t('game.needLogin') }}
              <FaButton variant="outline" @click="router.push('/data-source/cm')">
                {{ t('game.goLogin') }}
              </FaButton>
            </div>
            <div v-else-if="achievements.isInitialLoading.value" class="rounded-lg bg-muted h-40 animate-pulse" />
            <div v-else-if="achievements.data.value?.status === 'success' && achievements.data.value.summary.total > 0" class="space-y-4">
              <div class="p-4 border rounded-lg bg-card">
                <div class="text-sm mb-2 flex items-center justify-between">
                  <span>{{ t('game.achievementProgress', { unlocked: achievements.data.value.summary.unlocked, total: achievements.data.value.summary.total }) }}</span>
                  <span class="font-semibold">{{ (achievements.data.value.summary.percentage ?? 0).toFixed(1) }}%</span>
                </div>
                <FaProgress :model-value="achievements.data.value.summary.percentage ?? 0" />
              </div>
              <div class="gap-3 grid grid-cols-1 lg:grid-cols-3 md:grid-cols-2">
                <div v-for="a in recentUnlocks" :key="a.internalName" class="p-3 border rounded-lg bg-card flex gap-3 items-center">
                  <img :src="a.icon" class="rounded size-10" referrerpolicy="no-referrer" alt="">
                  <div class="min-w-0">
                    <div class="text-sm font-medium truncate">
                      {{ a.localizedName }}
                    </div>
                    <div class="text-xs text-muted-foreground">
                      {{ dayjs.unix(a.unlockTimeUtc!).format('YYYY-MM-DD HH:mm') }}
                      <template v-if="a.globalUnlockedPercent != null">
                        · {{ a.globalUnlockedPercent.toFixed(1) }}%
                      </template>
                    </div>
                  </div>
                </div>
              </div>
              <FaButton variant="outline" @click="router.push('/achievements')">
                {{ t('game.allAchievements') }}
              </FaButton>
            </div>
            <div v-else class="text-sm text-muted-foreground py-12 text-center">
              {{ t('game.noAchievements') }}
            </div>
          </template>
          <template #friends>
            <div v-if="friends.isInitialLoading.value" class="rounded-lg bg-muted h-40 animate-pulse" />
            <div v-else-if="playingFriends.length" class="gap-3 grid grid-cols-1 lg:grid-cols-3 md:grid-cols-2">
              <div v-for="friend in playingFriends" :key="friend.steamId" class="p-3 border rounded-lg bg-card flex gap-3 items-center">
                <FaAvatar :src="friend.avatarHash ? `https://avatars.fastly.steamstatic.com/${friend.avatarHash}_medium.jpg` : ''" :fallback="friend.personaName.slice(0, 1)" />
                <div class="min-w-0">
                  <div class="text-sm font-medium truncate">
                    {{ friend.personaName }}
                  </div>
                  <div class="text-xs text-green-600 dark:text-green-400">
                    {{ t('game.playingNow') }}
                  </div>
                </div>
              </div>
            </div>
            <div v-else class="text-sm text-muted-foreground py-12 text-center">
              {{ steamStore.selectedAccountName ? t('game.noFriends') : t('game.needLogin') }}
            </div>
          </template>
        </FaTabs>
      </template>
    </FaPageMain>
  </div>
</template>

<style scoped>
.banner {
  min-height: 220px;
  background: linear-gradient(135deg, oklch(var(--primary) / 80%), oklch(var(--primary) / 40%));
}

.banner-bg {
  filter: blur(2px);
  transform: scale(1.04);
}

.banner-mask {
  background: linear-gradient(to top, rgb(0 0 0 / 75%), rgb(0 0 0 / 25%));
}
</style>
