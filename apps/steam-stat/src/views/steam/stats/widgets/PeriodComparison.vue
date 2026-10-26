<script setup lang="ts">
import type { Totals } from '@/features/stats/aggregate'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { changeRatio, comparePeriods } from '@/features/stats/aggregate'
import { useStatsContext } from '@/features/stats/context'
import { formatRatio } from '@/features/stats/format'
import { useStatsStore } from '@/store/modules/stats'

const { t } = useI18n()
const ctx = useStatsContext()
const store = useStatsStore()

const comparisons = computed(() => comparePeriods(ctx.scoped.value, store.todayKey, ctx.opts.value))

const metrics: { key: keyof Totals, duration: boolean }[] = [
  { key: 'seconds', duration: true },
  { key: 'effective', duration: true },
  { key: 'count', duration: false },
  { key: 'activeDays', duration: false },
  { key: 'apps', duration: false },
]

function display(value: number, duration: boolean) {
  return duration ? ctx.fmt(value) : String(Math.round(value))
}

function csv() {
  return [
    [t('stats.column.period'), t('stats.column.metric'), t('stats.compare.current'), t('stats.compare.previous'), t('stats.compare.change')],
    ...comparisons.value.flatMap(c => metrics.map(m => [
      t(`stats.compare.${c.key}`),
      t(`stats.metric.${m.key}`),
      display(c.current.totals[m.key], m.duration),
      display(c.previous.totals[m.key], m.duration),
      formatRatio(changeRatio(c.current.totals[m.key], c.previous.totals[m.key])),
    ])),
  ]
}
</script>

<template>
  <StatChartCard :title="t('stats.widget.periodComparison')" icon="i-mdi:compare-horizontal" :csv="csv">
    <div class="space-y-3">
      <div v-for="c in comparisons" :key="c.key" class="p-3 rounded-md bg-muted/50">
        <div class="text-sm font-medium mb-2 flex flex-wrap gap-2 items-center justify-between">
          <span>{{ t(`stats.compare.${c.key}`) }}</span>
          <span class="text-xs text-muted-foreground font-normal">
            {{ c.current.from }} ~ {{ c.current.to }} vs {{ c.previous.from }} ~ {{ c.previous.to }}
          </span>
        </div>
        <div class="gap-2 grid grid-cols-2 md:grid-cols-5">
          <div v-for="m in metrics" :key="m.key">
            <div class="text-xs text-muted-foreground">
              {{ t(`stats.metric.${m.key}`) }}
            </div>
            <div class="flex flex-wrap gap-1 items-center">
              <span class="font-semibold tabular-nums">{{ display(c.current.totals[m.key], m.duration) }}</span>
              <FaTrend
                v-if="changeRatio(c.current.totals[m.key], c.previous.totals[m.key]) !== null && c.previous.totals[m.key] !== c.current.totals[m.key]"
                :value="formatRatio(changeRatio(c.current.totals[m.key], c.previous.totals[m.key]))"
                :type="c.current.totals[m.key] >= c.previous.totals[m.key] ? 'up' : 'down'"
                variant="soft"
              />
            </div>
            <div class="text-xs text-muted-foreground tabular-nums">
              {{ t('stats.compare.was', { value: display(c.previous.totals[m.key], m.duration) }) }}
            </div>
          </div>
        </div>
      </div>
    </div>
  </StatChartCard>
</template>
