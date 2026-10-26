<script setup lang="ts">
import type { MetaField } from '@/features/stats/aggregate'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { metaBreakdown } from '@/features/stats/aggregate'
import { namedBarOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'

const { t } = useI18n()
const ctx = useStatsContext()
const field = ref<MetaField>('developer')

const fieldOptions = computed(() => (['developer', 'publisher', 'type'] as const).map(value => ({ value, label: t(`stats.meta.${value}`) })))

const items = computed(() => metaBreakdown(ctx.sessions.value, ctx.data.value.apps, field.value, t('stats.meta.unknown')).slice(0, ctx.topN.value))
const option = computed(() => items.value.length ? namedBarOption(items.value, ctx.theme.value, ctx.fmt) : null)

const csv = () => [[t(`stats.meta.${field.value}`), t('stats.column.hours')], ...items.value.map(i => [i.name, (i.seconds / 3600).toFixed(2)])]
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.metaBreakdown')"
    icon="i-mdi:tag-multiple-outline"
    :description="t('stats.hint.meta')"
    :option="option"
    :csv="csv"
  >
    <template #actions>
      <FaSelect v-model="field" :options="fieldOptions" class="w-28" />
    </template>
  </StatChartCard>
</template>
