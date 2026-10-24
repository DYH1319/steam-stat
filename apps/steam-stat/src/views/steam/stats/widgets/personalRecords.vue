<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { personalRecords } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineProps<{ title: string, icon?: string, appId?: number }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()

const records = computed(() => personalRecords(ctx.allSessions.value, statsStore.prefs, dayjs().unix()))
const cards = computed(() => [
  { label: t('stats.recStreak'), value: records.value.longestStreak ? t('stats.days', { n: records.value.longestStreak.days }) : '—', sub: records.value.longestStreak ? `${records.value.longestStreak.from} ~ ${records.value.longestStreak.to}` : '' },
  { label: t('stats.recBreak'), value: records.value.longestBreak ? t('stats.days', { n: records.value.longestBreak.days }) : '—', sub: records.value.longestBreak ? `${records.value.longestBreak.from} ~ ${records.value.longestBreak.to}` : '' },
  { label: t('stats.recLongestSession'), value: records.value.longestSession ? formatSessionDuration(records.value.longestSession.duration) : '—', sub: records.value.longestSession ? ctx.appName(records.value.longestSession.appId) : '' },
  { label: t('stats.recLatestNight'), value: records.value.latestNight?.clock ?? '—', sub: records.value.latestNight ? ctx.appName(records.value.latestNight.session.appId) : '' },
  { label: t('stats.recMostPlayed'), value: records.value.mostPlayedDay ? formatSessionDuration(records.value.mostPlayedDay.seconds) : '—', sub: records.value.mostPlayedDay?.day ?? '' },
  { label: t('stats.recMostSessions'), value: records.value.mostSessionsDay ? String(records.value.mostSessionsDay.count) : '—', sub: records.value.mostSessionsDay?.day ?? '' },
  { label: t('stats.recMostGames'), value: records.value.mostGamesDay ? String(records.value.mostGamesDay.count) : '—', sub: records.value.mostGamesDay?.day ?? '' },
])
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :loading="ctx.loading.value" :empty="ctx.allSessions.value.length === 0" :empty-text="t('stats.empty')">
    <template #actions>
      <slot name="actions" />
    </template>
    <div class="gap-3 grid grid-cols-2 md:grid-cols-4">
      <div v-for="card in cards" :key="card.label" class="p-4 border rounded-lg">
        <div class="text-sm text-muted-foreground">
          {{ card.label }}
        </div>
        <div class="text-xl font-bold mt-1">
          {{ card.value }}
        </div>
        <div class="text-xs text-muted-foreground mt-1 truncate">
          {{ card.sub }}
        </div>
      </div>
    </div>
  </StatChartCard>
</template>
