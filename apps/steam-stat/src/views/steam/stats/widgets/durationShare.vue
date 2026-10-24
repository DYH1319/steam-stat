<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { topN } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import { shareOption, toHours } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()
const chartType = ref('pie')

const items = computed(() =>
  topN(ctx.totals.value, statsStore.prefs.topN, true).map(a => ({
    name: (a as { appId?: number }).appId == null ? t('stats.others') : ctx.appName(a.appId),
    appId: (a as { appId?: number }).appId,
    value: a.seconds,
    color: (a as { appId?: number }).appId != null ? ctx.appColor(a.appId) : undefined,
  })),
)

const option = computed(() => shareOption(theme.value, chartType.value, items.value).option)
const empty = computed(() => items.value.length === 0)

function csv() {
  return { headers: [t('history.columnApp'), t('stats.colHours')], rows: ctx.totals.value.map(a => [ctx.appName(a.appId), toHours(a.seconds)]) }
}

function onChartClick(p: any) {
  const item = items.value[p.dataIndex]
  if (item?.appId != null) {
    ctx.drill.value = { ...ctx.drill.value, appId: item.appId }
  }
}
</script>

<template>
  <StatChartCard v-model:chart-type="chartType" :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv" :chart-types="['pie', 'bar', 'treemap']" @chart-click="onChartClick">
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
