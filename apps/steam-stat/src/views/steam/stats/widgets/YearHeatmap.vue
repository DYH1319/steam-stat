<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { dailyTotals, filterSessions } from '@/features/stats/aggregate'
import { yearHeatmapOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'
import { dayKey } from '@/features/stats/time'

const { t } = useI18n()
const ctx = useStatsContext()

const sessions = computed(() => ctx.drill.value.appId == null
  ? ctx.scoped.value
  : filterSessions(ctx.scoped.value, { appId: ctx.drill.value.appId }, ctx.opts.value))

const years = computed(() => {
  const set = new Set<number>([Number(ctx.range.value.to.slice(0, 4))])
  for (const s of ctx.scoped.value) {
    set.add(Number(dayKey(s.start, ctx.opts.value).slice(0, 4)))
  }
  return [...set].sort((a, b) => b - a)
})

const year = ref<number>(Number(ctx.range.value.to.slice(0, 4)))
watch(years, (list) => {
  if (!list.includes(year.value)) {
    year.value = list[0]
  }
})

const yearOptions = computed(() => years.value.map(value => ({ value, label: String(value) })))

const entries = computed(() => [...dailyTotals(sessions.value, ctx.opts.value).entries()]
  .filter(([day]) => day.startsWith(`${year.value}-`)))

const yearTotal = computed(() => entries.value.reduce((sum, [, v]) => sum + v, 0))

const option = computed(() => yearHeatmapOption(
  year.value,
  entries.value,
  ctx.theme.value,
  ctx.fmt,
  ctx.opts.value.weekStart,
  t('stats.weekdaysShortSunday').split(','),
  t('stats.monthsShort').split(','),
))

function onClick(params: any) {
  const day = params?.value?.[0]
  if (typeof day === 'string') {
    ctx.setDrillDay(day)
  }
}

const csv = () => [[t('stats.column.date'), t('stats.column.hours')], ...entries.value.map(([day, seconds]) => [day, (seconds / 3600).toFixed(2)])]
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.yearHeatmap')"
    icon="i-mdi:calendar-month-outline"
    :description="t('stats.yearTotal', { year, value: ctx.fmt(yearTotal), days: entries.length })"
    :option="option"
    :height="200"
    :csv="csv"
    @chart-click="onClick"
  >
    <template #actions>
      <FaSelect v-model="year" :options="yearOptions" class="w-24" />
    </template>
  </StatChartCard>
</template>
