<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { useStatsContext } from '@/features/stats/context'
import dayjs from '@/utils/dayjs'
import { axis, tip, toHours } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const { theme } = useChartTheme()
const appFilter = ref<number>()

const appOptions = computed(() => [
  { value: undefined as unknown as number, label: t('stats.allApps') },
  ...ctx.totals.value.slice(0, 20).map(a => ({ value: a.appId, label: ctx.appName(a.appId) })),
])

const rows = computed(() =>
  ctx.rangeScoped.value
    .filter(p => appFilter.value == null || p.appId === appFilter.value)
    .sort((a, b) => a.startTime - b.startTime)
    .slice(-50),
)

const option = computed(() => ({
  tooltip: {
    ...tip(theme.value),
    formatter: (p: any) => `${dayjs.unix(rows.value[p.dataIndex].startTime).format('YYYY-MM-DD HH:mm')} · ${ctx.appName(rows.value[p.dataIndex].appId)} · ${toHours(rows.value[p.dataIndex].duration)}h`,
  },
  grid: { left: 40, right: 16, top: 16, bottom: 28 },
  xAxis: { type: 'category', data: rows.value.map(p => dayjs.unix(p.startTime).format('MM-DD HH:mm')), ...axis(theme.value) },
  yAxis: { type: 'value', name: 'h', ...axis(theme.value) },
  series: [{ type: 'line', symbolSize: 6, data: rows.value.map(p => toHours(p.duration)), lineStyle: { color: theme.value.primary }, itemStyle: { color: theme.value.primary } }],
}))
const empty = computed(() => rows.value.length === 0)

function csv() {
  return { headers: [t('history.columnApp'), t('history.columnStart'), 'hours'], rows: rows.value.map(p => [ctx.appName(p.appId), dayjs.unix(p.startTime).format('YYYY-MM-DD HH:mm'), toHours(p.duration)]) }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv">
    <template #header>
      <FaSelect v-model="appFilter" :options="appOptions" class="w-40" :placeholder="t('stats.filterApp')" />
    </template>
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
