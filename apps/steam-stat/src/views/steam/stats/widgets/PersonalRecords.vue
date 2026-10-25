<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { personalRecords } from '@/features/stats/aggregate'
import { useStatsContext } from '@/features/stats/context'
import { formatClockMinutes } from '@/features/stats/format'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'

const { t } = useI18n()
const ctx = useStatsContext()
const store = useStatsStore()

const records = computed(() => personalRecords(ctx.scoped.value, ctx.opts.value, store.todayKey))

const items = computed(() => {
  const r = records.value
  const span = (s?: { from: string, to: string } | null) => s ? `${s.from} ~ ${s.to}` : ''
  return [
    { key: 'longestStreak', icon: 'i-mdi:fire', value: r.longestStreak ? t('stats.daysCount', { n: r.longestStreak.days }) : '—', hint: span(r.longestStreak) },
    { key: 'currentStreak', icon: 'i-mdi:calendar-sync', value: r.currentStreak ? t('stats.daysCount', { n: r.currentStreak.days }) : '—', hint: span(r.currentStreak) },
    { key: 'longestBreak', icon: 'i-mdi:sleep', value: r.longestBreak ? t('stats.daysCount', { n: r.longestBreak.days }) : '—', hint: span(r.longestBreak) },
    {
      key: 'longestSession',
      icon: 'i-mdi:timer-sand',
      value: r.longestSession ? ctx.fmt(r.longestSession.duration) : '—',
      hint: r.longestSession ? `${ctx.appName(r.longestSession.appId)} · ${dayjs.unix(r.longestSession.start).format('YYYY-MM-DD')}` : '',
    },
    { key: 'mostPlayedDay', icon: 'i-mdi:calendar-star', value: r.mostPlayedDay ? ctx.fmt(r.mostPlayedDay.seconds) : '—', hint: r.mostPlayedDay?.day ?? '' },
    { key: 'mostSessionsDay', icon: 'i-mdi:repeat', value: r.mostSessionsDay ? t('stats.sessionsCount', { n: r.mostSessionsDay.count }) : '—', hint: r.mostSessionsDay?.day ?? '' },
    {
      key: 'latestNight',
      icon: 'i-mdi:weather-night',
      value: r.latestNight ? formatClockMinutes(r.latestNight.minutes) : '—',
      hint: r.latestNight ? `${ctx.appName(r.latestNight.session.appId)} · ${dayjs.unix(r.latestNight.session.end).format('YYYY-MM-DD')}` : '',
    },
    {
      key: 'earliestMorning',
      icon: 'i-mdi:weather-sunset-up',
      value: r.earliestMorning ? formatClockMinutes(r.earliestMorning.minutes) : '—',
      hint: r.earliestMorning ? `${ctx.appName(r.earliestMorning.session.appId)} · ${dayjs.unix(r.earliestMorning.session.start).format('YYYY-MM-DD')}` : '',
    },
    { key: 'activeDays', icon: 'i-mdi:calendar-check', value: `${r.activeDays} / ${r.totalDays}`, hint: r.totalDays ? `${((r.activeDays / r.totalDays) * 100).toFixed(0)}%` : '' },
  ]
})
</script>

<template>
  <StatChartCard :title="t('stats.widget.personalRecords')" icon="i-mdi:trophy-outline" :description="t('stats.hint.records')">
    <div class="gap-2 grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3">
      <div v-for="item in items" :key="item.key" class="p-3 rounded-md bg-muted/50">
        <div class="text-xs text-muted-foreground flex gap-1 items-center">
          <FaIcon :name="item.icon" class="text-primary" />
          {{ t(`stats.record.${item.key}`) }}
        </div>
        <div class="text-lg font-semibold tabular-nums">
          {{ item.value }}
        </div>
        <div class="text-xs text-muted-foreground truncate" :title="item.hint">
          {{ item.hint }}
        </div>
      </div>
    </div>
  </StatChartCard>
</template>
