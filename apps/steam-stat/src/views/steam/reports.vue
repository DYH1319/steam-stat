<script setup lang="ts">
import type { PeriodRange, PeriodUnit } from '@/features/stats/aggregate'
import type { ShareModel } from '@/features/stats/shareImage'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import GameImage from '@/components/GameImage/index.vue'
import ShareImageDialog from '@/components/ShareImageDialog/index.vue'
import { changeRatio, periodOf, periodReport, shiftPeriod } from '@/features/stats/aggregate'
import { distributionOption } from '@/features/stats/charts'
import { provideStatsContext } from '@/features/stats/context'
import { formatClockMinutes, formatRatio } from '@/features/stats/format'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import OnThisDay from './stats/widgets/OnThisDay.vue'
import '@/utils/echarts'

defineOptions({
  name: 'SteamReports',
})

const { t } = useI18n()
const router = useRouter()
const store = useStatsStore()
const ctx = provideStatsContext()

const unit = ref<PeriodUnit>('week')
const unitOptions = computed(() => (['week', 'month', 'year'] as const).map(value => ({ value, label: t(`reports.unit.${value}`) })))
const period = ref<PeriodRange>(periodOf(store.todayKey, 'week', store.timeOptions))

watch([unit, () => store.timeOptions], () => {
  period.value = periodOf(store.todayKey, unit.value, store.timeOptions)
})

function shift(delta: number) {
  period.value = shiftPeriod(period.value, delta, store.timeOptions)
}

const isCurrent = computed(() => period.value.to >= store.todayKey)

const periodLabel = computed(() => {
  const from = dayjs(period.value.from, 'YYYY-MM-DD')
  if (period.value.unit === 'year') {
    return t('reports.label.year', { year: from.year() })
  }
  if (period.value.unit === 'month') {
    return t('reports.label.month', { year: from.year(), month: from.month() + 1 })
  }
  return t('reports.label.week', { from: period.value.from, to: period.value.to })
})

const report = computed(() => periodReport(ctx.scoped.value, period.value, store.timeOptions))
const hasData = computed(() => report.value.totals.count > 0)
const topTotal = computed(() => report.value.topApps[0]?.seconds || 1)

const peakHour = computed(() => {
  const hours = report.value.hourDistribution
  const max = Math.max(...hours)
  return max > 0 ? hours.indexOf(max) : null
})

const weekdayLabels = computed(() => {
  const sundayFirst = t('stats.weekdaysShortSunday').split(',')
  return store.weekStart === 1 ? [...sundayFirst.slice(1), sundayFirst[0]] : sundayFirst
})

const peakWeekday = computed(() => {
  const days = report.value.weekdayDistribution
  const max = Math.max(...days)
  return max > 0 ? weekdayLabels.value[days.indexOf(max)] : null
})

function trend(value: number, previous: number) {
  const ratio = changeRatio(value, previous)
  return ratio == null || value === previous ? null : { value: formatRatio(ratio), up: value >= previous }
}

const dailyOption = computed(() => distributionOption(
  report.value.daily.keys.map(key => key.slice(5)),
  report.value.daily.values,
  ctx.theme.value,
  store.fmt,
))
const hourOption = computed(() => distributionOption(
  Array.from({ length: 24 }, (_, h) => String(h).padStart(2, '0')),
  report.value.hourDistribution,
  ctx.theme.value,
  store.fmt,
))

const slides = computed(() => {
  const list: string[] = ['cover']
  if (!hasData.value) {
    return list
  }
  list.push('topGames', 'busiest', 'routine', 'moments')
  if (report.value.newApps.length) {
    list.push('newGames')
  }
  list.push('ending')
  return list
})

const current = ref(0)
const direction = ref<'next' | 'prev'>('next')
watch(slides, () => {
  current.value = Math.min(current.value, slides.value.length - 1)
})
watch(period, () => {
  current.value = 0
})

function go(delta: number) {
  const next = current.value + delta
  if (next < 0 || next >= slides.value.length) {
    return
  }
  direction.value = delta > 0 ? 'next' : 'prev'
  current.value = next
}

const fullscreen = ref(false)
function onKeydown(event: KeyboardEvent) {
  if (!fullscreen.value) {
    return
  }
  if (event.key === 'ArrowRight' || event.key === 'PageDown' || event.key === ' ') {
    go(1)
  }
  else if (event.key === 'ArrowLeft' || event.key === 'PageUp') {
    go(-1)
  }
}
onMounted(() => window.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))

const gradients = computed(() => ctx.theme.value.gradients(slides.value.length + 1))
const slideBackground = computed(() => {
  const [a, b] = gradients.value[current.value] ?? gradients.value[0]
  return `linear-gradient(135deg, ${a}, ${b})`
})

// 分享图
const shareOpen = ref(false)
const shareModel = computed<ShareModel | null>(() => {
  const r = report.value
  if (!hasData.value) {
    return null
  }
  const accounts = ctx.data.value.users.filter(u => !store.steamIds.length || store.steamIds.includes(u.steamId))
  return {
    title: periodLabel.value,
    subtitle: t(`reports.title.${period.value.unit}`),
    account: accounts.map(u => u.personaName || u.accountName || u.steamId).join(' / '),
    summary: [
      { label: t('stats.summary.total'), value: store.fmt(r.totals.seconds) },
      { label: t('stats.summary.sessions'), value: String(r.totals.count) },
      { label: t('stats.summary.games'), value: String(r.totals.apps) },
      { label: t('stats.summary.activeDays'), value: String(r.totals.activeDays) },
    ],
    topGames: r.topApps.slice(0, 5).map(a => ({ name: ctx.appName(a.appId), value: store.fmt(a.seconds), ratio: a.seconds / topTotal.value })),
    highlights: [
      r.busiestDay && { label: t('reports.busiestDay'), value: store.fmt(r.busiestDay.seconds), hint: r.busiestDay.day },
      r.longestSession && { label: t('stats.record.longestSession'), value: store.fmt(r.longestSession.duration), hint: ctx.appName(r.longestSession.appId) },
      r.records.latestNight && { label: t('stats.record.latestNight'), value: formatClockMinutes(r.records.latestNight.minutes), hint: ctx.appName(r.records.latestNight.session.appId) },
      r.records.longestStreak && { label: t('stats.record.longestStreak'), value: t('stats.daysCount', { n: r.records.longestStreak.days }) },
    ].filter(Boolean) as ShareModel['highlights'],
    hours: (() => {
      const max = Math.max(1, ...r.hourDistribution)
      return r.hourDistribution.map(v => v / max)
    })(),
    footer: `Steam Stat · ${dayjs().format('YYYY-MM-DD')}`,
  }
})

onMounted(() => {
  store.ensureLoaded().catch((e: any) => toast.error(`${t('common.getFailed')}: ${e?.message || e}`))
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader icon="i-mdi:presentation" :title="t('menu.steamReports')" :description="t('reports.subtitle')">
        <template #actions>
          <FaButton variant="outline" :disabled="!shareModel" @click="shareOpen = true">
            <FaIcon name="i-mdi:share-variant-outline" />
            {{ t('share.title') }}
          </FaButton>
          <FaButton :disabled="!hasData" @click="fullscreen = true">
            <FaIcon name="i-mdi:play" />
            {{ t('reports.play') }}
          </FaButton>
        </template>
      </AppPageHeader>

      <div class="mb-4 flex flex-wrap gap-2 items-center">
        <FaSelect v-model="unit" :options="unitOptions" class="w-28" />
        <FaButton size="icon" variant="outline" @click="shift(-1)">
          <FaIcon name="i-mdi:chevron-left" />
        </FaButton>
        <div class="font-semibold px-2 text-center min-w-48">
          {{ periodLabel }}
        </div>
        <FaButton size="icon" variant="outline" :disabled="isCurrent" @click="shift(1)">
          <FaIcon name="i-mdi:chevron-right" />
        </FaButton>
        <FaButton variant="ghost" @click="period = periodOf(store.todayKey, unit, store.timeOptions)">
          {{ t('reports.current') }}
        </FaButton>
      </div>

      <div v-if="store.loading && !store.loadedAt" class="rounded-xl bg-muted h-[420px] animate-pulse" />
      <div v-else class="gap-4 grid grid-cols-1 xl:grid-cols-3">
        <div class="xl:col-span-2">
          <div
            class="slides text-white rounded-xl relative overflow-hidden"
            :class="fullscreen ? 'slides-fullscreen' : 'h-[460px]'"
            :style="{ backgroundImage: slideBackground }"
          >
            <Transition :name="`slide-${direction}`" mode="out-in">
              <div :key="slides[current]" class="slide p-8 flex flex-col h-full justify-center">
                <!-- 封面 -->
                <template v-if="slides[current] === 'cover'">
                  <div class="text-sm opacity-80">
                    {{ t(`reports.title.${period.unit}`) }}
                  </div>
                  <div class="text-4xl font-bold mt-1">
                    {{ periodLabel }}
                  </div>
                  <template v-if="hasData">
                    <div class="text-6xl font-extrabold mt-8 tabular-nums">
                      {{ store.fmt(report.totals.seconds) }}
                    </div>
                    <div class="mt-2 opacity-90 flex flex-wrap gap-2 items-center">
                      {{ t('reports.coverLine', { sessions: report.totals.count, games: report.totals.apps, days: report.totals.activeDays }) }}
                      <span v-if="trend(report.totals.seconds, report.previousTotals.seconds)" class="text-sm px-2 py-0.5 rounded-full bg-white/20">
                        {{ trend(report.totals.seconds, report.previousTotals.seconds)!.up ? '▲' : '▼' }}
                        {{ trend(report.totals.seconds, report.previousTotals.seconds)!.value }}
                        {{ t(`reports.vsPrevious.${period.unit}`) }}
                      </span>
                    </div>
                  </template>
                  <div v-else class="text-lg mt-8 opacity-90">
                    {{ t('reports.noData') }}
                  </div>
                </template>

                <!-- 最常玩 -->
                <template v-else-if="slides[current] === 'topGames'">
                  <div class="text-2xl font-bold mb-6">
                    {{ t('reports.topGamesTitle') }}
                  </div>
                  <div class="space-y-3">
                    <div v-for="(app, index) in report.topApps.slice(0, 5)" :key="app.appId" class="flex gap-3 items-center">
                      <span class="text-xl font-bold text-center w-6">{{ index + 1 }}</span>
                      <GameImage :app-id="app.appId" :name="ctx.appName(app.appId)" />
                      <div class="flex-1 min-w-0">
                        <div class="flex justify-between">
                          <span class="truncate">{{ ctx.appName(app.appId) }}</span>
                          <span class="tabular-nums">{{ store.fmt(app.seconds) }}</span>
                        </div>
                        <div class="mt-1 rounded-full bg-white/20 h-2">
                          <div class="rounded-full bg-white h-2" :style="{ width: `${(app.seconds / topTotal) * 100}%` }" />
                        </div>
                      </div>
                    </div>
                  </div>
                </template>

                <!-- 最忙的一天 -->
                <template v-else-if="slides[current] === 'busiest'">
                  <div class="text-2xl font-bold">
                    {{ t('reports.busiestTitle') }}
                  </div>
                  <div v-if="report.busiestDay" class="text-lg mt-2 opacity-90">
                    {{ t('reports.busiestLine', { day: report.busiestDay.day, value: store.fmt(report.busiestDay.seconds) }) }}
                  </div>
                  <div class="mt-4 p-2 rounded-lg bg-white/90 flex-1 min-h-0 dark:bg-black/40">
                    <VChart :option="dailyOption" autoresize class="h-full min-h-48 w-full" />
                  </div>
                </template>

                <!-- 作息 -->
                <template v-else-if="slides[current] === 'routine'">
                  <div class="text-2xl font-bold">
                    {{ t('reports.routineTitle') }}
                  </div>
                  <div class="text-lg mt-2 opacity-90">
                    {{ t('reports.routineLine', { hour: peakHour == null ? '—' : `${String(peakHour).padStart(2, '0')}:00`, weekday: peakWeekday ?? '—' }) }}
                  </div>
                  <div class="mt-4 p-2 rounded-lg bg-white/90 flex-1 min-h-0 dark:bg-black/40">
                    <VChart :option="hourOption" autoresize class="h-full min-h-48 w-full" />
                  </div>
                </template>

                <!-- 高光时刻 -->
                <template v-else-if="slides[current] === 'moments'">
                  <div class="text-2xl font-bold mb-6">
                    {{ t('reports.momentsTitle') }}
                  </div>
                  <div class="gap-4 grid grid-cols-1 md:grid-cols-2">
                    <div v-if="report.longestSession" class="p-4 rounded-lg bg-white/15">
                      <div class="text-sm opacity-80">
                        {{ t('stats.record.longestSession') }}
                      </div>
                      <div class="text-3xl font-bold tabular-nums">
                        {{ store.fmt(report.longestSession.duration) }}
                      </div>
                      <div class="text-sm opacity-80">
                        {{ ctx.appName(report.longestSession.appId) }} · {{ dayjs.unix(report.longestSession.start).format('MM-DD HH:mm') }}
                      </div>
                    </div>
                    <div v-if="report.records.latestNight" class="p-4 rounded-lg bg-white/15">
                      <div class="text-sm opacity-80">
                        {{ t('stats.record.latestNight') }}
                      </div>
                      <div class="text-3xl font-bold tabular-nums">
                        {{ formatClockMinutes(report.records.latestNight.minutes) }}
                      </div>
                      <div class="text-sm opacity-80">
                        {{ ctx.appName(report.records.latestNight.session.appId) }}
                      </div>
                    </div>
                    <div v-if="report.records.longestStreak" class="p-4 rounded-lg bg-white/15">
                      <div class="text-sm opacity-80">
                        {{ t('stats.record.longestStreak') }}
                      </div>
                      <div class="text-3xl font-bold">
                        {{ t('stats.daysCount', { n: report.records.longestStreak.days }) }}
                      </div>
                      <div class="text-sm opacity-80">
                        {{ report.records.longestStreak.from }} ~ {{ report.records.longestStreak.to }}
                      </div>
                    </div>
                    <div class="p-4 rounded-lg bg-white/15">
                      <div class="text-sm opacity-80">
                        {{ t('stats.summary.effective') }}
                      </div>
                      <div class="text-3xl font-bold tabular-nums">
                        {{ store.fmt(report.totals.effective) }}
                      </div>
                      <div class="text-sm opacity-80">
                        {{ t('stats.summary.afk', { value: store.fmt(report.totals.idle) }) }}
                      </div>
                    </div>
                  </div>
                </template>

                <!-- 新游戏 -->
                <template v-else-if="slides[current] === 'newGames'">
                  <div class="text-2xl font-bold">
                    {{ t('reports.newGamesTitle', { n: report.newApps.length }) }}
                  </div>
                  <div class="mt-6 flex flex-wrap gap-3">
                    <div v-for="appId in report.newApps.slice(0, 12)" :key="appId" class="p-2 rounded-lg bg-white/15 flex gap-2 items-center">
                      <GameImage :app-id="appId" :name="ctx.appName(appId)" />
                      <span class="text-sm">{{ ctx.appName(appId) }}</span>
                    </div>
                  </div>
                </template>

                <!-- 结尾 -->
                <template v-else>
                  <div class="text-3xl font-bold">
                    {{ t('reports.endingTitle') }}
                  </div>
                  <div class="text-lg mt-3 opacity-90">
                    {{ t('reports.endingLine', { value: store.fmt(report.totals.seconds), game: report.topApps[0] ? ctx.appName(report.topApps[0].appId) : '—' }) }}
                  </div>
                  <div class="mt-6 flex gap-2">
                    <FaButton variant="secondary" @click="shareOpen = true">
                      <FaIcon name="i-mdi:share-variant-outline" />
                      {{ t('share.title') }}
                    </FaButton>
                    <FaButton v-if="report.topApps[0]" variant="secondary" @click="fullscreen = false; router.push(`/game/${report.topApps[0].appId}`)">
                      <FaIcon name="i-mdi:open-in-app" />
                      {{ t('stats.openGame') }}
                    </FaButton>
                  </div>
                </template>
              </div>
            </Transition>

            <div class="flex gap-2 bottom-4 right-4 absolute">
              <FaButton size="icon" variant="secondary" :disabled="current === 0" @click="go(-1)">
                <FaIcon name="i-mdi:chevron-left" />
              </FaButton>
              <FaButton size="icon" variant="secondary" :disabled="current >= slides.length - 1" @click="go(1)">
                <FaIcon name="i-mdi:chevron-right" />
              </FaButton>
              <FaButton v-if="fullscreen" size="icon" variant="secondary" @click="fullscreen = false">
                <FaIcon name="i-mdi:close" />
              </FaButton>
            </div>
            <div class="flex gap-1.5 bottom-6 left-8 absolute">
              <button
                v-for="(slide, index) in slides"
                :key="slide"
                class="rounded-full h-2 transition-all"
                :class="index === current ? 'bg-white w-6' : 'bg-white/40 w-2'"
                @click="direction = index > current ? 'next' : 'prev'; current = index"
              />
            </div>
          </div>
        </div>
        <OnThisDay />
      </div>
    </FaPageMain>

    <ShareImageDialog v-model="shareOpen" :model="shareModel" />
  </div>
</template>

<style scoped>
.slides-fullscreen {
  position: fixed;
  inset: 0;
  z-index: 2000;
  border-radius: 0;
}

.slide {
  padding-bottom: 4.5rem;
}

.slide-next-enter-active,
.slide-next-leave-active,
.slide-prev-enter-active,
.slide-prev-leave-active {
  transition: all 0.35s ease;
}

.slide-next-enter-from,
.slide-prev-leave-to {
  opacity: 0;
  transform: translateX(40px);
}

.slide-next-leave-to,
.slide-prev-enter-from {
  opacity: 0;
  transform: translateX(-40px);
}

@media (prefers-reduced-motion: reduce) {
  .slide-next-enter-active,
  .slide-next-leave-active,
  .slide-prev-enter-active,
  .slide-prev-leave-active {
    transition: none;
  }
}
</style>
