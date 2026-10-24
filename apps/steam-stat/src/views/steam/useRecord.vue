<script setup lang="ts">
import type { StatsDashboardLayout } from '@/store/modules/stats'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { toast } from 'vue-sonner'
import { useChartTheme } from '@/composables/useChartTheme'
import { createStatsContext, provideStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { widgetById, widgetRegistry } from './stats/widgets'

defineOptions({
  name: 'SteamUseRecord',
})

const { t } = useI18n()
const router = useRouter()
const statsStore = useStatsStore()
const { theme } = useChartTheme()

const ctx = createStatsContext({
  prefs: computed(() => statsStore.prefs),
  palette: computed(() => theme.value.palette),
})
provideStatsContext(ctx)

const activeTab = ref('dashboard')
const editing = ref(false)
const settingsVisible = ref(false)
const renameVisible = ref(false)
const renameValue = ref('')

const userOptions = computed(() => ctx.users.value.map(u => ({
  value: u.steamId,
  label: `${u.personaName ?? u.accountName} (${u.accountName})`,
})))
const appOptions = computed(() => ctx.totals.value.map(a => ({ value: a.appId, label: ctx.appName(a.appId) })))

const shortcuts = computed(() => [
  { text: t('stats.range1d'), value: () => [dayjs().subtract(1, 'day'), dayjs()] },
  { text: t('stats.range2d'), value: () => [dayjs().subtract(2, 'day'), dayjs()] },
  { text: t('stats.range3d'), value: () => [dayjs().subtract(3, 'day'), dayjs()] },
  { text: t('stats.range7d'), value: () => [dayjs().subtract(7, 'day'), dayjs()] },
  { text: t('stats.range14d'), value: () => [dayjs().subtract(14, 'day'), dayjs()] },
  { text: t('stats.range30d'), value: () => [dayjs().subtract(30, 'day'), dayjs()] },
  { text: t('stats.range60d'), value: () => [dayjs().subtract(60, 'day'), dayjs()] },
  { text: t('stats.range90d'), value: () => [dayjs().subtract(90, 'day'), dayjs()] },
  { text: t('stats.range6m'), value: () => [dayjs().subtract(6, 'month'), dayjs()] },
  { text: t('stats.range1y'), value: () => [dayjs().subtract(1, 'year'), dayjs()] },
])

const layouts = computed(() => statsStore.dashboard.layouts)
const activeLayout = computed(() =>
  layouts.value.find(l => l.id === statsStore.dashboard.activeLayoutId) ?? layouts.value[0],
)
const layoutOptions = computed(() => layouts.value.map(l => ({ value: l.id, label: l.name })))

function setActiveLayout(id: string) {
  statsStore.dashboard.activeLayoutId = id
}

function mutateLayout(fn: (layout: StatsDashboardLayout) => void) {
  const layout = activeLayout.value
  if (layout) {
    fn(layout)
  }
}

function newLayout() {
  const id = `layout-${Date.now()}`
  statsStore.dashboard.layouts.push({ id, name: t('stats.layoutNew', { n: layouts.value.length + 1 }), widgets: [] })
  statsStore.dashboard.activeLayoutId = id
}
function renameLayout() {
  renameValue.value = activeLayout.value?.name ?? ''
  renameVisible.value = true
}
function confirmRename() {
  if (activeLayout.value && renameValue.value.trim()) {
    activeLayout.value.name = renameValue.value.trim()
  }
  renameVisible.value = false
}
function duplicateLayout() {
  const layout = activeLayout.value
  if (!layout) {
    return
  }
  const id = `layout-${Date.now()}`
  statsStore.dashboard.layouts.push({ id, name: `${layout.name} ${t('stats.layoutCopy')}`, widgets: layout.widgets.map(w => ({ ...w })) })
  statsStore.dashboard.activeLayoutId = id
}
function deleteLayout() {
  if (layouts.value.length <= 1) {
    toast.error(t('stats.layoutKeepOne'))
    return
  }
  statsStore.dashboard.layouts = layouts.value.filter(l => l.id !== activeLayout.value.id)
  statsStore.dashboard.activeLayoutId = statsStore.dashboard.layouts[0].id
}
function addWidget(widgetId: string) {
  mutateLayout((l) => {
    l.widgets.push({ key: `w-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`, widgetId, span: widgetById.get(widgetId)?.defaultSpan ?? 1 })
  })
}
function removeWidget(key: string) {
  mutateLayout(l => l.widgets = l.widgets.filter(w => w.key !== key))
}
function toggleSpan(key: string) {
  mutateLayout(l => l.widgets.forEach((w) => {
    if (w.key === key) {
      w.span = w.span === 1 ? 2 : 1
    }
  }))
}
const dragKey = ref<string>()
function onDrop(key: string) {
  if (!dragKey.value || dragKey.value === key) {
    return
  }
  mutateLayout((l) => {
    const from = l.widgets.findIndex(w => w.key === dragKey.value)
    const to = l.widgets.findIndex(w => w.key === key)
    if (from >= 0 && to >= 0) {
      const [moved] = l.widgets.splice(from, 1)
      l.widgets.splice(to, 0, moved)
    }
  })
}
const missingWidgets = computed(() => widgetRegistry.filter(w => !activeLayout.value?.widgets.some(x => x.widgetId === w.id)))

function relativeTime(ts: number): string {
  if (!ts) {
    return '—'
  }
  const s = Math.max(0, Math.round((Date.now() - ts) / 1000))
  if (s < 60) {
    return `${s}s`
  }
  if (s < 3600) {
    return `${Math.floor(s / 60)}m`
  }
  return `${Math.floor(s / 3600)}h`
}

interface TabWidget { key: string, widgetId: string, span: 1 | 2, props?: Record<string, unknown> }
const tabs = computed((): { value: string, label: string, widgets: TabWidget[] }[] => [
  { value: 'dashboard', label: t('stats.tabDashboard'), widgets: [] },
  { value: 'duration', label: t('stats.tabDuration'), widgets: [
    { key: 'summary', widgetId: 'summary', span: 2 },
    { key: 'trend', widgetId: 'timeTrend', span: 2 },
    { key: 'share', widgetId: 'durationShare', span: 1 },
    { key: 'launch', widgetId: 'launchFrequency', span: 1 },
    { key: 'recent', widgetId: 'recentSessions', span: 2 },
    { key: 'drill', widgetId: 'drillDetail', span: 2 },
  ] },
  { value: 'schedule', label: t('stats.tabSchedule'), widgets: [
    { key: 'year', widgetId: 'yearHeatmap', span: 2 },
    { key: 'hour', widgetId: 'hourWeekHeatmap', span: 1 },
    { key: 'gantt', widgetId: 'sessionGantt', span: 2 },
  ] },
  { value: 'games', label: t('stats.tabGames'), widgets: [
    { key: 'life', widgetId: 'lifecycle', span: 2 },
    { key: 'meta', widgetId: 'metaBreakdown', span: 1 },
    { key: 'share2', widgetId: 'durationShare', span: 1 },
    { key: 'rank', widgetId: 'gameRanking', span: 2 },
  ] },
  { value: 'records', label: t('stats.tabRecords'), widgets: [
    { key: 'rec', widgetId: 'personalRecords', span: 2 },
  ] },
  { value: 'compare', label: t('stats.tabCompare'), widgets: [
    { key: 'cmp', widgetId: 'periodComparison', span: 2 },
  ] },
])

const hasSessions = computed(() => ctx.allSessions.value.length > 0)

watch(() => statsStore.filters.steamIds, ctx.refresh, { deep: true })
onMounted(() => ctx.refresh())
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader
        icon="i-uil:statistics"
        :title="t('menu.steamUsage')"
        :description="t('stats.subtitle')"
      >
        <template #actions>
          <FaTooltip :text="dayjs(ctx.lastUpdateTime.value).format('YYYY-MM-DD HH:mm:ss')" :disabled="!ctx.lastUpdateTime.value">
            <span class="text-sm text-muted-foreground">{{ relativeTime(ctx.lastUpdateTime.value) }}</span>
          </FaTooltip>
          <FaButton variant="outline" :loading="ctx.loading.value" @click="ctx.refresh">
            <FaIcon name="i-mdi:refresh" />
            {{ t('common.refreshData') }}
          </FaButton>
          <FaPopover v-model="settingsVisible" side="bottom" align="end">
            <template #trigger>
              <FaButton variant="outline">
                <FaIcon name="i-mdi:cog-outline" />
                {{ t('stats.settings') }}
              </FaButton>
            </template>
            <div class="p-3 w-64 space-y-3">
              <div class="flex gap-2 items-center justify-between">
                <span class="text-sm">{{ t('stats.dayStartHour') }}</span>
                <FaSelect v-model="statsStore.prefs.dayStartHour" :options="[0, 1, 2, 3, 4, 5, 6].map(h => ({ value: h, label: `${h}:00` }))" class="w-24" />
              </div>
              <div class="flex gap-2 items-center justify-between">
                <span class="text-sm">{{ t('stats.weekStartsOn') }}</span>
                <FaSelect v-model="statsStore.prefs.weekStartsOn" :options="[{ value: 1, label: t('stats.monday') }, { value: 0, label: t('stats.sunday') }]" class="w-28" />
              </div>
              <div class="flex gap-2 items-center justify-between">
                <span class="text-sm">TopN</span>
                <FaSelect v-model="statsStore.prefs.topN" :options="[5, 10, 15, 20].map(n => ({ value: n, label: String(n) }))" class="w-24" />
              </div>
            </div>
          </FaPopover>
        </template>
      </AppPageHeader>

      <div class="mb-4 flex flex-wrap gap-3 items-center">
        <FaSelect v-model="statsStore.filters.steamIds" multiple :options="userOptions" :placeholder="t('sessions.filterUser')" class="min-w-44" />
        <el-date-picker
          v-model="statsStore.filters.dateRange"
          type="daterange"
          value-format="YYYY-MM-DD"
          :shortcuts="shortcuts"
          :start-placeholder="t('useRecord.startDate')"
          :end-placeholder="t('useRecord.endDate')"
        />
        <FaSelect v-model="statsStore.filters.includeAppIds" multiple :options="appOptions" :placeholder="t('stats.onlyApps')" class="min-w-44" />
        <FaSelect v-model="statsStore.filters.excludeAppIds" multiple :options="appOptions" :placeholder="t('stats.excludeApps')" class="min-w-44" />
        <FaButton variant="outline" @click="statsStore.filters = { steamIds: [], dateRange: null, includeAppIds: [], excludeAppIds: [] }">
          <FaIcon name="i-mdi:restart" />
          {{ t('sessions.resetFilter') }}
        </FaButton>
        <FaTag v-if="ctx.drill.value.day" variant="secondary" class="cursor-pointer" @click="ctx.drill.value = { ...ctx.drill.value, day: undefined }">
          {{ ctx.drill.value.day }} ×
        </FaTag>
        <FaTag v-if="ctx.drill.value.appId != null" variant="secondary" class="cursor-pointer" @click="ctx.drill.value = { ...ctx.drill.value, appId: undefined }">
          {{ ctx.appName(ctx.drill.value.appId) }} ×
        </FaTag>
      </div>

      <p class="text-xs text-muted-foreground mb-4">
        {{ t('stats.cleaningHint') }}
      </p>

      <div v-if="!hasSessions && !ctx.loading.value" class="text-muted-foreground py-16 flex flex-col gap-3 items-center">
        <span class="i-uil:statistics opacity-40 h-20 w-20 inline-block" />
        <span>{{ t('stats.empty') }}</span>
        <div class="flex gap-2">
          <FaButton variant="outline" @click="router.push('/setting')">
            {{ t('stats.goSettings') }}
          </FaButton>
          <FaButton variant="outline" @click="router.push('/history')">
            {{ t('stats.goHistory') }}
          </FaButton>
        </div>
      </div>

      <template v-else>
        <FaTabs v-model="activeTab" :list="tabs.map(x => ({ value: x.value, label: x.label }))" class="mb-3" />

        <!-- 仪表盘 -->
        <template v-if="activeTab === 'dashboard'">
          <div class="mb-3 flex flex-wrap gap-2 items-center">
            <FaSelect :model-value="activeLayout?.id" :options="layoutOptions" class="w-44" @update:model-value="v => setActiveLayout(String(v))" />
            <FaButton variant="ghost" size="icon-sm" :title="t('stats.layoutNewBtn')" @click="newLayout">
              <FaIcon name="i-mdi:plus" />
            </FaButton>
            <FaButton variant="ghost" size="icon-sm" :title="t('stats.layoutRename')" @click="renameLayout">
              <FaIcon name="i-mdi:pencil-outline" />
            </FaButton>
            <FaButton variant="ghost" size="icon-sm" :title="t('stats.layoutDuplicate')" @click="duplicateLayout">
              <FaIcon name="i-mdi:content-copy" />
            </FaButton>
            <FaButton variant="ghost" size="icon-sm" :title="t('stats.layoutDelete')" @click="deleteLayout">
              <FaIcon name="i-mdi:delete-outline" />
            </FaButton>
            <FaButton variant="outline" @click="editing = !editing">
              {{ editing ? t('stats.layoutDone') : t('stats.layoutEdit') }}
            </FaButton>
            <FaSelect v-if="editing" :options="missingWidgets.map(w => ({ value: w.id, label: t(w.titleKey) }))" :placeholder="t('stats.addWidget')" class="w-44" @update:model-value="v => v != null && addWidget(String(v))" />
          </div>
          <div class="gap-4 grid grid-cols-1 xl:grid-cols-2">
            <div
              v-for="w in activeLayout?.widgets"
              :key="w.key"
              :class="w.span === 2 ? 'xl:col-span-2' : ''"
              :draggable="editing"
              @dragstart="dragKey = w.key"
              @dragover.prevent
              @drop="onDrop(w.key)"
            >
              <component :is="widgetById.get(w.widgetId)?.component" :title="t(widgetById.get(w.widgetId)?.titleKey ?? '')" :icon="widgetById.get(w.widgetId)?.icon">
                <template v-if="editing" #actions>
                  <FaButton variant="ghost" size="icon-sm" title="span" @click="toggleSpan(w.key)">
                    <FaIcon name="i-mdi:arrow-left-right" />
                  </FaButton>
                  <FaButton variant="ghost" size="icon-sm" @click="removeWidget(w.key)">
                    <FaIcon name="i-mdi:close" />
                  </FaButton>
                </template>
              </component>
            </div>
          </div>
        </template>

        <!-- 固定页签 -->
        <div v-else class="gap-4 grid grid-cols-1 xl:grid-cols-2">
          <div v-for="w in tabs.find(x => x.value === activeTab)?.widgets" :key="w.key" :class="w.span === 2 ? 'xl:col-span-2' : ''">
            <component :is="widgetById.get(w.widgetId)?.component" :title="t(widgetById.get(w.widgetId)?.titleKey ?? '')" :icon="widgetById.get(w.widgetId)?.icon" v-bind="w.props" />
          </div>
        </div>
      </template>
    </FaPageMain>

    <FaModal v-model="renameVisible" :title="t('stats.layoutRename')">
      <FaInput v-model="renameValue" class="w-full" @keydown.enter="confirmRename" />
      <template #footer>
        <FaButton variant="outline" @click="renameVisible = false">
          {{ t('common.cancel') }}
        </FaButton>
        <FaButton @click="confirmRename">
          {{ t('common.confirm') }}
        </FaButton>
      </template>
    </FaModal>
  </div>
</template>
