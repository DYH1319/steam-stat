<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import { axis, tip } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()

const items = computed(() =>
  [...ctx.totals.value].sort((a, b) => b.count - a.count).slice(0, statsStore.prefs.topN),
)

const option = computed(() => ({
  tooltip: tip(theme.value),
  grid: { left: 40, right: 16, top: 16, bottom: 28 },
  xAxis: { type: 'category', data: items.value.map(i => ctx.appName(i.appId)), ...axis(theme.value) },
  yAxis: { type: 'value', ...axis(theme.value) },
  series: [{ type: 'bar', data: items.value.map(i => ({ value: i.count, itemStyle: { color: ctx.appColor(i.appId) } })) }],
}))
const empty = computed(() => items.value.length === 0)

function csv() {
  return { headers: [t('history.columnApp'), t('stats.colSessions')], rows: items.value.map(i => [ctx.appName(i.appId), i.count]) }
}

function onChartClick(p: any) {
  const item = items.value[p.dataIndex]
  if (item) {
    ctx.drill.value = { ...ctx.drill.value, appId: item.appId }
  }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv" @chart-click="onChartClick">
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
