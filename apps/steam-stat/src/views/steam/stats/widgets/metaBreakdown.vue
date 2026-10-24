<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { groupByMeta, topN } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import { shareOption, toHours } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()
const field = ref<'developer' | 'publisher' | 'type'>('developer')
const chartType = ref('pie')

const fieldOptions = computed(() => [
  { value: 'developer', label: t('stats.metaDeveloper') },
  { value: 'publisher', label: t('stats.metaPublisher') },
  { value: 'type', label: t('stats.metaType') },
])

const items = computed(() =>
  topN(groupByMeta(ctx.totals.value, ctx.appMeta.value, field.value), statsStore.prefs.topN, true)
    .map(g => ({ name: g.name === '__unknown__' ? t('stats.metaUnknown') : g.name, value: g.seconds })),
)

const option = computed(() => shareOption(theme.value, chartType.value, items.value).option)
const empty = computed(() => items.value.length === 0)

function csv() {
  return { headers: [t(`stats.meta_${field.value}`), 'hours'], rows: items.value.map(i => [i.name, toHours(i.value)]) }
}
</script>

<template>
  <StatChartCard v-model:chart-type="chartType" :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv" :chart-types="['pie', 'bar', 'treemap']">
    <template #header>
      <FaSelect v-model="field" :options="fieldOptions" class="w-36" />
    </template>
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
