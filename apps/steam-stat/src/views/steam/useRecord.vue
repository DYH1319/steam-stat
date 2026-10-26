<script setup lang="ts">
import type { DashboardWidget } from '@/store/modules/stats'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { provideStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import StatsDashboard from './stats/StatsDashboard.vue'
import StatsFilterBar from './stats/StatsFilterBar.vue'
import WidgetGrid from './stats/WidgetGrid.vue'

defineOptions({
  name: 'SteamUseRecord',
})

const { t } = useI18n()
const router = useRouter()
const store = useStatsStore()
const ctx = provideStatsContext()

const tab = ref<string>('dashboard')

const tabs = computed(() => [
  { value: 'dashboard', label: t('stats.tab.dashboard'), icon: 'i-mdi:view-dashboard-outline' },
  { value: 'duration', label: t('stats.tab.duration'), icon: 'i-mdi:clock-outline' },
  { value: 'routine', label: t('stats.tab.routine'), icon: 'i-mdi:calendar-clock' },
  { value: 'games', label: t('stats.tab.games'), icon: 'i-mdi:controller' },
  { value: 'records', label: t('stats.tab.records'), icon: 'i-mdi:trophy-outline' },
  { value: 'compare', label: t('stats.tab.compare'), icon: 'i-mdi:compare-horizontal' },
])

const tabWidgets: Record<string, DashboardWidget[]> = {
  duration: [
    { id: 'summary', span: 2 },
    { id: 'timeTrend', span: 2 },
    { id: 'durationShare', span: 1 },
    { id: 'launchFrequency', span: 1 },
    { id: 'recentSessions', span: 2 },
    { id: 'drillDetail', span: 2 },
  ],
  routine: [
    { id: 'yearHeatmap', span: 2 },
    { id: 'hourWeekHeatmap', span: 1 },
    { id: 'onThisDay', span: 1 },
    { id: 'sessionGantt', span: 2 },
  ],
  games: [
    { id: 'gameRanking', span: 2 },
    { id: 'lifecycle', span: 1 },
    { id: 'metaBreakdown', span: 1 },
    { id: 'durationShare', span: 1 },
    { id: 'launchFrequency', span: 1 },
  ],
  records: [
    { id: 'personalRecords', span: 2 },
    { id: 'onThisDay', span: 1 },
    { id: 'sessionGantt', span: 1 },
  ],
  compare: [
    { id: 'periodComparison', span: 2 },
    { id: 'timeTrend', span: 2 },
  ],
}

const lastUpdate = computed(() => store.data.lastUpdateTime ? dayjs.unix(store.data.lastUpdateTime).format('YYYY-MM-DD HH:mm:ss') : null)
const hasAnyData = computed(() => store.data.sessions.length > 0)

async function refresh(showToast: boolean) {
  try {
    await store.reload()
    if (showToast) {
      toast.success(t('useRecord.getSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

onMounted(() => {
  void (store.loadedAt ? Promise.resolve() : refresh(false))
})

onActivated(() => {
  // 页签保活：回到页面时数据超过 1 分钟则静默刷新
  if (store.loadedAt && Date.now() - store.loadedAt > 60_000) {
    void refresh(false)
  }
})

watch(() => ctx.drill.value.appId ?? ctx.drill.value.day, (value) => {
  if (value != null && tab.value !== 'dashboard' && tab.value !== 'duration') {
    tab.value = 'duration'
  }
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader icon="i-mdi:chart-box" :title="t('menu.steamUsage')" :description="t('stats.subtitle')">
        <template #actions>
          <FaTooltip :text="t('useRecord.lastUpdateTip')">
            <span v-if="lastUpdate" class="text-xs text-muted-foreground flex gap-1 items-center">
              <FaIcon name="i-mdi:database-clock-outline" />
              {{ t('stats.freshness', { time: lastUpdate }) }}
            </span>
          </FaTooltip>
          <FaButton variant="outline" @click="router.push('/reports')">
            <FaIcon name="i-mdi:presentation" />
            {{ t('menu.steamReports') }}
          </FaButton>
          <FaButton variant="outline" @click="router.push('/history')">
            <FaIcon name="i-mdi:database-import-outline" />
            {{ t('menu.steamHistory') }}
          </FaButton>
          <FaButton :loading="store.loading" @click="refresh(true)">
            <FaIcon name="i-mdi:refresh" />
            {{ t('common.refreshData') }}
          </FaButton>
        </template>
      </AppPageHeader>

      <div v-if="store.loading && !store.loadedAt" class="gap-4 grid grid-cols-1 xl:grid-cols-2">
        <div v-for="i in 4" :key="i" class="rounded-lg bg-muted h-72 animate-pulse" />
      </div>

      <div v-else-if="!hasAnyData" class="py-16 text-center flex flex-col gap-3 items-center">
        <FaIcon name="i-mdi:gamepad-variant-outline" class="text-primary opacity-60 size-20" />
        <div class="text-lg font-semibold">
          {{ t('stats.emptyTitle') }}
        </div>
        <div class="text-sm text-muted-foreground max-w-md">
          {{ t('stats.emptyDesc') }}
        </div>
        <div class="flex gap-2">
          <FaButton @click="router.push('/setting')">
            <FaIcon name="i-mdi:radar" />
            {{ t('stats.emptyEnable') }}
          </FaButton>
          <FaButton variant="outline" @click="router.push('/history')">
            <FaIcon name="i-mdi:database-import-outline" />
            {{ t('stats.emptyImport') }}
          </FaButton>
        </div>
      </div>

      <template v-else>
        <StatsFilterBar />
        <FaTabs v-model="tab" :list="tabs" list-class="justify-start w-fit" content-class="pt-4">
          <template #dashboard>
            <StatsDashboard />
          </template>
          <template v-for="(widgets, key) in tabWidgets" :key="key" #[key]>
            <WidgetGrid :widgets="widgets" />
          </template>
        </FaTabs>
        <div class="text-xs text-muted-foreground mt-4 text-right">
          {{ t('useRecord.cleaningRulesHint') }}
        </div>
      </template>
    </FaPageMain>
  </div>
</template>
