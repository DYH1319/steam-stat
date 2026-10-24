<script setup lang="ts">
import type { SessionInput } from '@/features/stats'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useChartTheme } from '@/composables/useChartTheme'
import { clipSessions, totalsByApp } from '@/features/stats'
import { createStatsContext, provideStatsContext } from '@/features/stats/context'
import { useDataSourceStore } from '@/store/modules/dataSource'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'
import HourWeekWidget from './stats/widgets/hourWeekHeatmap.vue'
import LifecycleWidget from './stats/widgets/lifecycle.vue'
import RecordsWidget from './stats/widgets/personalRecords.vue'
import TimeTrendWidget from './stats/widgets/timeTrend.vue'
import YearHeatmapWidget from './stats/widgets/yearHeatmap.vue'

defineOptions({
  name: 'SteamGame',
})

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const statsStore = useStatsStore()
const dataSourceStore = useDataSourceStore()
const { theme } = useChartTheme()
const electronApi = (window as Window).electron

const appId = computed(() => Number(route.params.appId))
const fixedAppId = computed(() => appId.value)

const ctx = createStatsContext({
  prefs: computed(() => statsStore.prefs),
  palette: computed(() => theme.value.palette),
  fixedAppId: fixedAppId as any,
  applyDateRange: false,
  applyIncludeExclude: false,
  localDrill: true,
})
provideStatsContext(ctx)

const activeTab = ref('overview')
const bannerFailed = ref(false)
const sessions = ref<SessionRecord[]>([])
const sessionsTotal = ref(0)
const page = ref(1)
const pageSize = 15

// 成就
const cmOk = computed(() => dataSourceStore.statusOf('cm.session') === 'ok')
const accounts = ref<string[]>([])
const accountName = ref<string>()
const achievements = ref<SteamAchievementGameResult | null>(null)
const achieveLoading = ref(false)

const appName = computed(() => ctx.appName(appId.value))

const stats = computed(() => {
  const pieces = clipSessions(ctx.allSessions.value, 0, Number.MAX_SAFE_INTEGER)
  const total = totalsByApp(pieces)[0]
  const sorted = pieces.map(p => p.session).sort((a, b) => a.startTime - b.startTime)
  const longest = sorted.reduce<SessionInput | null>((m, s) => s.duration! > (m?.duration ?? -1) ? s : m, null)
  return {
    seconds: total?.seconds ?? 0,
    effective: total?.effectiveSeconds ?? 0,
    count: total?.count ?? 0,
    first: sorted[0]?.startTime,
    last: sorted[sorted.length - 1]?.endTime ?? undefined,
    avg: total && total.count > 0 ? total.seconds / total.count : 0,
    longest: longest?.duration ?? 0,
  }
})

const preInstall = computed(() => ctx.preInstallByApp.value.get(appId.value) ?? 0)
const officialTotal = computed(() => stats.value.seconds + preInstall.value)

const unlocked = computed(() => achievements.value?.achievements.filter(a => a.isUnlocked) ?? [])
const recentUnlocks = computed(() =>
  [...unlocked.value]
    .filter(a => a.unlockTimeUtc)
    .sort((a, b) => (b.unlockTimeUtc ?? 0) - (a.unlockTimeUtc ?? 0))
    .slice(0, 10),
)

async function loadSessions() {
  const result = await electronApi.sessionListGet({
    appIds: [appId.value],
    page: page.value,
    pageSize,
    sortField: 'startTime',
    sortOrder: 'desc',
  })
  sessions.value = result.items
  sessionsTotal.value = result.total
}

async function loadAchievements() {
  if (!accountName.value) {
    return
  }
  achieveLoading.value = true
  try {
    achievements.value = await electronApi.steamAchievementsGameGet({ accountName: accountName.value, appId: appId.value })
  }
  finally {
    achieveLoading.value = false
  }
}

async function init() {
  bannerFailed.value = false
  await ctx.refresh()
  await loadSessions()
  if (cmOk.value) {
    accounts.value = await electronApi.steamLoginLoggedInUsersGet()
    accountName.value ??= accounts.value[0]
    await loadAchievements()
  }
}

function appDisplayName(row: SessionRecord): string {
  if (row.appNameLocalized) {
    try {
      const map = JSON.parse(row.appNameLocalized) as Record<string, string>
      return map['zh-CN'] ?? map['en-US'] ?? row.appName ?? `App ${row.appId}`
    }
    catch {
      // 回退原名
    }
  }
  return row.appName ?? `App ${row.appId}`
}

watch(() => route.params.appId, init)
watch(page, loadSessions)
watch(accountName, loadAchievements)
onMounted(init)
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <!-- 横幅 -->
      <div class="mb-6 rounded-lg h-48 relative overflow-hidden from-primary/60 to-primary/20 bg-gradient-to-br">
        <img
          v-if="!bannerFailed"
          :src="`https://steamcdn-a.akamaihd.net/steam/apps/${appId}/header.jpg`"
          class="h-full w-full object-cover"
          @error="bannerFailed = true"
        >
        <div class="p-4 flex items-end inset-0 absolute from-black/70 to-transparent bg-gradient-to-t">
          <h1 class="text-2xl text-white font-bold">
            {{ appName }}
          </h1>
        </div>
      </div>

      <div class="mb-4 flex flex-wrap gap-2">
        <FaButton variant="outline" @click="electronApi.shellOpenExternal(`https://store.steampowered.com/app/${appId}`)">
          <FaIcon name="i-mdi:steam" /> {{ t('game.openStore') }}
        </FaButton>
        <FaButton variant="outline" @click="router.push({ path: '/sessions', query: { appId: String(appId) } })">
          {{ t('stats.viewInSessions') }}
        </FaButton>
      </div>

      <!-- 关键数字 -->
      <div class="mb-6 gap-3 grid grid-cols-2 md:grid-cols-4 xl:grid-cols-8">
        <div
          v-for="card in [
            { label: t('stats.cardTotal'), value: formatSessionDuration(stats.seconds) },
            { label: t('stats.cardEffective'), value: formatSessionDuration(stats.effective) },
            { label: t('stats.cardSessions'), value: String(stats.count) },
            { label: t('game.avgSession'), value: formatSessionDuration(stats.avg) },
            { label: t('stats.recLongestSession'), value: formatSessionDuration(stats.longest) },
            { label: t('game.firstPlayed'), value: stats.first ? dayjs.unix(stats.first).format('YYYY-MM-DD') : '—' },
            { label: t('stats.colLastPlayed'), value: stats.last ? dayjs.unix(stats.last).format('YYYY-MM-DD') : '—' },
            { label: t('game.officialTotal'), value: preInstall > 0 ? formatSessionDuration(officialTotal) : null, hide: preInstall <= 0 },
          ].filter(c => !c.hide)" :key="card.label" class="p-3 border rounded-lg"
        >
          <div class="text-xs text-muted-foreground">
            {{ card.label }}
          </div>
          <div class="font-bold mt-1">
            {{ card.value }}
          </div>
        </div>
      </div>

      <FaTabs
        v-model="activeTab" :list="[
          { value: 'overview', label: t('game.tabOverview') },
          { value: 'sessions', label: t('game.tabSessions') },
          { value: 'achievements', label: t('game.tabAchievements') },
        ]" class="mb-4"
      />

      <!-- 概览 -->
      <div v-if="activeTab === 'overview'" class="gap-4 grid grid-cols-1 xl:grid-cols-2">
        <div class="xl:col-span-2">
          <LifecycleWidget :title="t('stats.widgetLifecycle')" :single-app-id="appId" />
        </div>
        <div class="xl:col-span-2">
          <TimeTrendWidget :title="t('stats.widgetTimeTrend')" default-granularity="month" />
        </div>
        <HourWeekWidget :title="t('stats.widgetHourWeekHeatmap')" />
        <YearHeatmapWidget :title="t('stats.widgetYearHeatmap')" />
        <div class="xl:col-span-2">
          <RecordsWidget :title="t('stats.widgetPersonalRecords')" />
        </div>
      </div>

      <!-- 会话 -->
      <div v-else-if="activeTab === 'sessions'">
        <div class="border rounded-lg overflow-auto">
          <table class="text-sm w-full">
            <thead class="text-muted-foreground bg-muted">
              <tr>
                <th class="px-3 py-2 text-left">
                  {{ t('history.columnApp') }}
                </th>
                <th class="text-left">
                  {{ t('history.columnStart') }}
                </th>
                <th class="text-left">
                  {{ t('history.columnEnd') }}
                </th>
                <th class="text-right">
                  {{ t('history.columnDuration') }}
                </th>
                <th class="text-right">
                  {{ t('history.columnAfk') }}
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="row in sessions" :key="row.id" class="border-t">
                <td class="px-3 py-1">
                  {{ appDisplayName(row) }}
                </td>
                <td>{{ row.startTime ? dayjs.unix(row.startTime).format('YYYY-MM-DD HH:mm') : '—' }}</td>
                <td>{{ row.endTime ? dayjs.unix(row.endTime).format('YYYY-MM-DD HH:mm') : t('sessions.ongoing') }}</td>
                <td class="text-right">
                  {{ formatSessionDuration(row.duration ?? 0) }}
                </td>
                <td class="text-right">
                  {{ formatSessionDuration(row.idleDuration) }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <FaPagination v-model:page="page" :total="sessionsTotal" :size="pageSize" class="mt-4" />
      </div>

      <!-- 成就 -->
      <div v-else-if="activeTab === 'achievements'">
        <div v-if="!cmOk" class="text-muted-foreground py-12 flex flex-col gap-3 items-center">
          <FaIcon name="i-mdi:trophy-off" class="opacity-40 h-14 w-14" />
          <span>{{ t('game.cmUnavailable') }}</span>
          <FaButton variant="outline" @click="router.push('/dataSource')">
            {{ t('game.goDataSource') }}
          </FaButton>
        </div>
        <template v-else>
          <div class="mb-3 flex gap-3 items-center">
            <FaSelect v-model="accountName" :options="accounts.map(a => ({ value: a, label: a }))" class="w-56" />
            <template v-if="achievements">
              <span class="text-sm">{{ unlocked.length }} / {{ achievements.achievements.length }} {{ t('game.unlocked') }}</span>
            </template>
            <FaIcon v-if="achieveLoading" name="i-mdi:loading" class="animate-spin" />
          </div>
          <div class="gap-2 grid grid-cols-1 md:grid-cols-2">
            <div v-for="a in recentUnlocks" :key="a.internalName" class="p-3 border rounded-lg flex gap-3 items-center">
              <img :src="a.icon" class="h-10 w-10">
              <div class="min-w-0">
                <div class="text-sm font-medium truncate">
                  {{ a.localizedName }}
                </div>
                <div class="text-xs text-muted-foreground">
                  {{ a.unlockTimeUtc ? dayjs.unix(a.unlockTimeUtc).format('YYYY-MM-DD HH:mm') : '' }}
                </div>
              </div>
            </div>
          </div>
          <p v-if="!achieveLoading && recentUnlocks.length === 0" class="text-sm text-muted-foreground py-8 text-center">
            {{ t('game.noAchievements') }}
          </p>
        </template>
      </div>
    </FaPageMain>
  </div>
</template>
