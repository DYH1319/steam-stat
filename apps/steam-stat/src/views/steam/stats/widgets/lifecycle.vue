<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { lifecycle } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { axis, tip } from './helpers'

const props = defineProps<{ title: string, icon?: string, singleAppId?: number }>()

const { t } = useI18n()
const ctx = useStatsContext()
const { theme } = useChartTheme()
const selected = ref<number[]>([])
const withPreInstall = ref(false)

const appOptions = computed(() => ctx.totals.value.slice(0, 20).map(a => ({ value: a.appId, label: ctx.appName(a.appId) })))
const appIds = computed(() => props.singleAppId != null
  ? [props.singleAppId]
  : (selected.value.length ? selected.value : ctx.totals.value.slice(0, 3).map(a => a.appId)))
const data = computed(() => lifecycle(ctx.allSessions.value, appIds.value, withPreInstall.value ? ctx.preInstallByApp.value : undefined))

const option = computed(() => ({
  tooltip: { ...tip(theme.value, 'axis'), valueFormatter: (v: any) => `${Math.round(v / 360) / 10}h` },
  legend: { textStyle: { color: theme.value.mutedText }, top: 0 },
  grid: { left: 50, right: 16, top: 30, bottom: 28 },
  xAxis: { type: 'time', ...axis(theme.value) },
  yAxis: { type: 'value', name: 'h', axisLabel: { color: theme.value.mutedText, formatter: (v: number) => `${Math.round(v / 3600)}` }, splitLine: { lineStyle: { color: theme.value.border, opacity: 0.4 } }, axisLine: { lineStyle: { color: theme.value.border } } },
  series: appIds.value.filter(id => data.value.has(id)).map(id => ({
    type: 'line',
    name: ctx.appName(id),
    showSymbol: false,
    step: 'end',
    data: data.value.get(id)!.map(p => [p.time * 1000, p.cumulative]),
    itemStyle: { color: ctx.appColor(id) },
  })),
}))
const empty = computed(() => data.value.size === 0)

function csv() {
  const rows: (string | number)[][] = []
  for (const [id, points] of data.value) {
    for (const p of points) {
      rows.push([ctx.appName(id), new Date(p.time * 1000).toISOString(), Math.round(p.cumulative / 36) / 100])
    }
  }
  return { headers: [t('history.columnApp'), 'time', 'hours'], rows }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv">
    <template #header>
      <FaSelect v-if="singleAppId == null" v-model="selected" multiple :options="appOptions" :placeholder="t('stats.filterApp')" class="min-w-48" />
      <label class="text-sm flex gap-2 items-center">
        <FaSwitch v-model="withPreInstall" />{{ t('stats.withPreInstall') }}
      </label>
    </template>
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
