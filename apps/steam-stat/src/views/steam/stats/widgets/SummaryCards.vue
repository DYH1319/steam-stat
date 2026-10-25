<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { totals } from '@/features/stats/aggregate'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'

const { t } = useI18n()
const ctx = useStatsContext()
const store = useStatsStore()

const summary = computed(() => totals(ctx.sessions.value, ctx.opts.value))

const baseline = computed(() => {
  const steamIds = store.steamIds.length ? new Set(store.steamIds) : null
  const appId = ctx.fixedAppId.value ?? ctx.drill.value.appId
  return ctx.data.value.baselines
    .filter(b => (!steamIds || steamIds.has(b.steamId)) && (appId == null || b.appId === appId))
    .reduce((sum, b) => ({ seconds: sum.seconds + b.baselineSeconds, untracked: sum.untracked + Math.max(0, b.untrackedSeconds) }), { seconds: 0, untracked: 0 })
})

const cards = computed(() => {
  const s = summary.value
  const list = [
    { key: 'total', icon: 'i-mdi:clock-outline', value: ctx.fmt(s.seconds), hint: t('stats.summary.afk', { value: ctx.fmt(s.idle) }) },
    { key: 'effective', icon: 'i-mdi:gamepad-variant-outline', value: ctx.fmt(s.effective), hint: s.seconds ? `${((s.effective / s.seconds) * 100).toFixed(0)}%` : '' },
    { key: 'sessions', icon: 'i-mdi:play-circle-outline', value: String(s.count), hint: t('stats.summary.avg', { value: ctx.fmt(s.avgSeconds) }) },
    { key: 'games', icon: 'i-mdi:controller', value: String(s.apps), hint: '' },
    { key: 'activeDays', icon: 'i-mdi:calendar-check-outline', value: String(s.activeDays), hint: s.activeDays ? t('stats.summary.perDay', { value: ctx.fmt(s.seconds / s.activeDays) }) : '' },
  ]
  if (baseline.value.seconds > 0) {
    list.push({ key: 'baseline', icon: 'i-mdi:history', value: ctx.fmt(baseline.value.seconds), hint: baseline.value.untracked > 0 ? t('stats.summary.untracked', { value: ctx.fmt(baseline.value.untracked) }) : '' })
  }
  return list
})

const gradients = computed(() => ctx.theme.value.gradients(cards.value.length))
</script>

<template>
  <div class="gap-3 grid grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
    <TransitionGroup name="card-in" appear>
      <div
        v-for="(card, index) in cards"
        :key="card.key"
        class="summary-card text-white p-4 rounded-lg"
        :style="{ backgroundImage: `linear-gradient(135deg, ${gradients[index][0]}, ${gradients[index][1]})`, transitionDelay: `${index * 40}ms` }"
      >
        <div class="text-sm opacity-90 flex gap-1.5 items-center">
          <FaIcon :name="card.icon" />
          {{ t(`stats.summary.${card.key}`) }}
        </div>
        <div class="text-2xl font-bold mt-1 tabular-nums">
          {{ card.value }}
        </div>
        <div class="text-xs mt-1 opacity-80 min-h-4">
          {{ card.hint }}
        </div>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.summary-card {
  transition: transform 0.25s ease, box-shadow 0.25s ease;
}

.summary-card:hover {
  box-shadow: 0 10px 24px -12px rgb(0 0 0 / 40%);
  transform: translateY(-2px);
}

.card-in-enter-active {
  transition: all 0.4s ease;
}

.card-in-enter-from {
  opacity: 0;
  transform: translateY(8px);
}

@media (prefers-reduced-motion: reduce) {
  .summary-card,
  .card-in-enter-active {
    transition: none;
  }
}
</style>
