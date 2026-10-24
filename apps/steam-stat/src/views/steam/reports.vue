<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import ShareImageDialog from '@/components/ShareImageDialog/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { splitByLogicalDay } from '@/features/stats'
import { createStatsContext, provideStatsContext } from '@/features/stats/context'
import { buildPeriodReport, buildYearReview, onThisDay } from '@/features/stats/report'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'
import { axis, tip, toHours } from './stats/widgets/helpers'
import '@/utils/echarts'

defineOptions({
  name: 'SteamReports',
})

const { t } = useI18n()
const statsStore = useStatsStore()
const { theme } = useChartTheme()

const ctx = createStatsContext({
  prefs: computed(() => statsStore.prefs),
  palette: computed(() => theme.value.palette),
  applyDateRange: false,
  applyIncludeExclude: false,
  localDrill: true,
})
provideStatsContext(ctx)

const activeTab = ref('period')
const periodMode = ref<'week' | 'month'>('week')
const offset = ref(1) // 默认上一完整周期

// ── 周期报告 ──────────────────────────────────────────────
const periodRange = computed(() => {
  const prefs = statsStore.prefs
  const now = dayjs()
  if (periodMode.value === 'week') {
    const weekStart = now.subtract((now.day() - prefs.weekStartsOn + 7) % 7, 'day').startOf('day')
    const start = weekStart.subtract(offset.value, 'week')
    return [start, start.add(7, 'day')] as const
  }
  const start = now.startOf('month').subtract(offset.value, 'month')
  return [start, start.add(1, 'month')] as const
})

const periodReport = computed(() => {
  const [s, e] = periodRange.value
  const span = e.unix() - s.unix()
  return buildPeriodReport(ctx.allSessions.value, s.unix(), e.unix(), s.unix() - span, s.unix(), statsStore.prefs)
})

const periodLabel = computed(() =>
  `${periodRange.value[0].format('YYYY-MM-DD')} ~ ${periodRange.value[1].subtract(1, 'day').format('YYYY-MM-DD')}`,
)

const dailyOption = computed(() => ({
  tooltip: tip(theme.value, 'axis'),
  grid: { left: 40, right: 16, top: 16, bottom: 28 },
  xAxis: { type: 'category', data: periodReport.value.daily.map(d => d.day.slice(5)), ...axis(theme.value) },
  yAxis: { type: 'value', name: 'h', ...axis(theme.value) },
  series: [{ type: 'bar', data: periodReport.value.daily.map(d => toHours(d.seconds)), itemStyle: { color: theme.value.primary } }],
}))
const hourlyOption = computed(() => ({
  tooltip: tip(theme.value, 'axis'),
  grid: { left: 40, right: 16, top: 16, bottom: 28 },
  xAxis: { type: 'category', data: Array.from({ length: 24 }, (_, i) => `${i}`), ...axis(theme.value) },
  yAxis: { type: 'value', name: 'h', ...axis(theme.value) },
  series: [{ type: 'bar', data: periodReport.value.hourly.map(toHours), itemStyle: { color: theme.value.palette[4] ?? theme.value.primary } }],
}))

// ── 年度回顾 ──────────────────────────────────────────────
const years = computed(() =>
  [...new Set([...splitByLogicalDay(ctx.allSessions.value, statsStore.prefs).keys()].map(k => Number(k.slice(0, 4))))]
    .sort((a, b) => b - a)
    .map(y => ({ value: y, label: String(y) })),
)
const year = ref(dayjs().year())
const review = computed(() => buildYearReview(ctx.allSessions.value, year.value, statsStore.prefs))
const monthlyOption = computed(() => ({
  tooltip: tip(theme.value, 'axis'),
  grid: { left: 40, right: 16, top: 16, bottom: 28 },
  xAxis: { type: 'category', data: review.value.monthly.map((_, i) => `${i + 1}月`), ...axis(theme.value) },
  yAxis: { type: 'value', name: 'h', ...axis(theme.value) },
  series: [{ type: 'bar', data: review.value.monthly.map(toHours), itemStyle: { color: theme.value.primary } }],
}))
const calendarOption = computed(() => ({
  tooltip: { ...tip(theme.value), formatter: (p: any) => `${p.data[0]} · ${Math.round(p.data[1] / 36) / 100}h` },
  visualMap: { min: 0, max: review.value.calendar.reduce((m, d) => Math.max(m, d[1]), 1) / 3600, inRange: { color: [theme.value.background, theme.value.primary] }, show: false },
  calendar: { range: year.value, cellSize: ['auto', 13], yearLabel: { show: false } },
  series: [{ type: 'heatmap', coordinateSystem: 'calendar', data: review.value.calendar.map(([k, s]) => [k, toHours(s)]) }],
}))

// 幻灯片
const slideshow = ref(false)
const slide = ref(0)
const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches ?? false
const slides = computed(() => [
  { title: t('report.yearTotal'), body: `${formatSessionDuration(review.value.seconds)} · ${t('stats.days', { n: review.value.daysPlayed })}` },
  { title: t('report.topGame'), body: review.value.topGame ? ctx.appName(review.value.topGame.appId) : '—', chart: null },
  { title: t('report.top5'), body: '', top5: review.value.top5 },
  { title: t('report.monthlyDist'), body: '', chart: 'monthly' },
  { title: t('report.calendar'), body: '', chart: 'calendar' },
  { title: t('report.nightOwl'), body: review.value.latestNight?.clock ?? '—', body2: review.value.longestSession ? `${t('stats.recLongestSession')}: ${formatSessionDuration(review.value.longestSession.duration)}` : '' },
  { title: t('stats.recStreak'), body: review.value.longestStreak ? `${t('stats.days', { n: review.value.longestStreak.days })} (${review.value.longestStreak.from} ~ ${review.value.longestStreak.to})` : '—' },
  { title: t('report.newGames'), body: String(review.value.newGames) },
  { title: t('report.closing'), body: `${year.value} · ${formatSessionDuration(review.value.seconds)}` },
])
function onSlideKey(e: KeyboardEvent) {
  if (e.key === 'Escape') {
    slideshow.value = false
  }
  else if (e.key === 'ArrowLeft') {
    slide.value = Math.max(0, slide.value - 1)
  }
  else if (e.key === 'ArrowRight' || e.key === ' ') {
    slide.value = Math.min(slides.value.length - 1, slide.value + 1)
  }
}
function openSlideshow() {
  slide.value = 0
  slideshow.value = true
}

// ── 历史上的今天 ──────────────────────────────────────────
const thisDay = ref<Date>(dayjs().toDate())
const thisDayMonthDay = computed(() => dayjs(thisDay.value).format('MM-DD'))
const thisDayRows = computed(() => onThisDay(ctx.allSessions.value, thisDayMonthDay.value, statsStore.prefs, dayjs().year()))

// ── 分享图 ────────────────────────────────────────────────
const shareVisible = ref(false)
const shareProps = ref<{ title: string, subtitle: string, stats: { label: string, value: string }[], topGames?: { name: string, value: number, color: string }[], chart?: { label: string, value: number }[], records?: { label: string, value: string }[] }>({ title: '', subtitle: '', stats: [] })

function sharePeriod() {
  const r = periodReport.value
  shareProps.value = {
    title: periodMode.value === 'week' ? t('report.weekly') : t('report.monthly'),
    subtitle: periodLabel.value,
    stats: [
      { label: t('stats.cardTotal'), value: formatSessionDuration(r.seconds) },
      { label: t('stats.cardSessions'), value: String(r.sessions) },
      { label: t('stats.cardGames'), value: String(r.games) },
      { label: t('report.newGames'), value: String(r.newGames.length) },
    ],
    topGames: r.topGames.map(g => ({ name: ctx.appName(g.appId), value: g.seconds, color: ctx.appColor(g.appId) })),
    chart: r.daily.map(d => ({ label: d.day.slice(5), value: d.seconds })),
    records: [
      { label: t('stats.recLongestSession'), value: r.longestSession ? formatSessionDuration(r.longestSession.duration) : '—' },
      { label: t('report.busiestDay'), value: r.busiestDay ? `${r.busiestDay.day} (${formatSessionDuration(r.busiestDay.seconds)})` : '—' },
    ],
  }
  shareVisible.value = true
}
function shareYear() {
  const r = review.value
  shareProps.value = {
    title: t('report.yearReview', { year: year.value }),
    subtitle: '',
    stats: [
      { label: t('stats.cardTotal'), value: formatSessionDuration(r.seconds) },
      { label: t('stats.cardGames'), value: String(r.games) },
      { label: t('stats.cardSessions'), value: String(r.sessions) },
      { label: t('stats.days', { n: r.daysPlayed }), value: '' },
    ],
    topGames: r.top5.map(g => ({ name: ctx.appName(g.appId), value: g.seconds, color: ctx.appColor(g.appId) })),
    chart: r.monthly.map((s, i) => ({ label: `${i + 1}`, value: s })),
    records: [
      { label: t('stats.recStreak'), value: r.longestStreak ? t('stats.days', { n: r.longestStreak.days }) : '—' },
      { label: t('report.nightOwl'), value: r.latestNight?.clock ?? '—' },
      { label: t('report.newGames'), value: String(r.newGames) },
    ],
  }
  shareVisible.value = true
}

onMounted(() => {
  void ctx.refresh()
  window.addEventListener('keydown', onSlideKey)
})
onBeforeUnmount(() => window.removeEventListener('keydown', onSlideKey))
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader icon="i-mdi:file-chart-outline" :title="t('menu.steamReports')" :description="t('report.subtitle')">
        <template #actions>
          <FaButton variant="outline" :loading="ctx.loading.value" @click="ctx.refresh">
            <FaIcon name="i-mdi:refresh" />{{ t('common.refreshData') }}
          </FaButton>
        </template>
      </AppPageHeader>

      <FaTabs
        v-model="activeTab" :list="[
          { value: 'period', label: t('report.tabPeriod') },
          { value: 'year', label: t('report.tabYear') },
          { value: 'onthisday', label: t('report.tabThisDay') },
        ]" class="mb-4"
      />

      <!-- 周/月报 -->
      <template v-if="activeTab === 'period'">
        <div class="mb-4 flex flex-wrap gap-3 items-center">
          <FaSelect v-model="periodMode" :options="[{ value: 'week', label: t('report.weekly') }, { value: 'month', label: t('report.monthly') }]" class="w-32" />
          <FaButton variant="ghost" size="icon-sm" :disabled="offset >= 52" @click="offset++">
            <FaIcon name="i-mdi:chevron-left" />
          </FaButton>
          <span class="text-sm">{{ periodLabel }}</span>
          <FaButton variant="ghost" size="icon-sm" :disabled="offset <= 0" @click="offset--">
            <FaIcon name="i-mdi:chevron-right" />
          </FaButton>
          <FaButton variant="outline" class="ml-auto" @click="sharePeriod">
            <FaIcon name="i-mdi:share-variant" />{{ t('report.shareImage') }}
          </FaButton>
        </div>
        <div class="gap-4 grid grid-cols-1 xl:grid-cols-2">
          <div class="card-shadow p-4 rounded-lg bg-card xl:col-span-2">
            <div class="flex flex-wrap gap-6">
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.cardTotal') }}
                </div>
                <div class="text-2xl font-bold">
                  {{ formatSessionDuration(periodReport.seconds) }}
                </div>
                <FaTrend v-if="periodReport.prev.seconds > 0" :type="periodReport.seconds >= periodReport.prev.seconds ? 'up' : 'down'" :value="`${Math.abs(Math.round((periodReport.seconds - periodReport.prev.seconds) / periodReport.prev.seconds * 100))}%`" />
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.cardSessions') }}
                </div><div class="text-2xl font-bold">
                  {{ periodReport.sessions }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.cardGames') }}
                </div><div class="text-2xl font-bold">
                  {{ periodReport.games }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('report.newGames') }}
                </div><div class="text-2xl font-bold">
                  {{ periodReport.newGames.length }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.recLongestSession') }}
                </div><div class="text-2xl font-bold">
                  {{ periodReport.longestSession ? formatSessionDuration(periodReport.longestSession.duration) : '—' }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('report.busiestDay') }}
                </div><div class="text-2xl font-bold">
                  {{ periodReport.busiestDay?.day ?? '—' }}
                </div>
              </div>
            </div>
            <div class="mt-4 space-y-1">
              <div v-for="g in periodReport.topGames" :key="g.appId" class="text-sm flex gap-2 items-center">
                <span class="w-40 truncate">{{ ctx.appName(g.appId) }}</span>
                <div class="rounded h-4" :style="{ width: `${Math.max(2, g.seconds / (periodReport.topGames[0]?.seconds || 1) * 60)}%`, background: ctx.appColor(g.appId) }" />
                <span class="text-muted-foreground">{{ formatSessionDuration(g.seconds) }}</span>
              </div>
            </div>
          </div>
          <div class="card-shadow p-4 rounded-lg bg-card">
            <div class="text-sm font-medium mb-2">
              {{ t('report.dailyDist') }}
            </div>
            <VChart :option="dailyOption" autoresize class="h-56" />
          </div>
          <div class="card-shadow p-4 rounded-lg bg-card">
            <div class="text-sm font-medium mb-2">
              {{ t('report.hourDist') }}
            </div>
            <VChart :option="hourlyOption" autoresize class="h-56" />
          </div>
        </div>
      </template>

      <!-- 年度回顾 -->
      <template v-else-if="activeTab === 'year'">
        <div class="mb-4 flex flex-wrap gap-3 items-center">
          <FaSelect v-model="year" :options="years" class="w-32" />
          <FaButton variant="outline" @click="openSlideshow">
            <FaIcon name="i-mdi:play" />{{ t('report.play') }}
          </FaButton>
          <FaButton variant="outline" @click="shareYear">
            <FaIcon name="i-mdi:share-variant" />{{ t('report.shareImage') }}
          </FaButton>
        </div>
        <div class="gap-4 grid grid-cols-1 xl:grid-cols-2">
          <div class="card-shadow p-4 rounded-lg bg-card xl:col-span-2">
            <div class="gap-4 grid grid-cols-2 md:grid-cols-4">
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.cardTotal') }}
                </div><div class="text-2xl font-bold">
                  {{ formatSessionDuration(review.seconds) }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('report.daysPlayed') }}
                </div><div class="text-2xl font-bold">
                  {{ review.daysPlayed }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.cardSessions') }}
                </div><div class="text-2xl font-bold">
                  {{ review.sessions }}
                </div>
              </div>
              <div>
                <div class="text-sm text-muted-foreground">
                  {{ t('stats.cardGames') }}
                </div><div class="text-2xl font-bold">
                  {{ review.games }}
                </div>
              </div>
            </div>
            <div class="mt-4 space-y-1">
              <div v-for="g in review.top5" :key="g.appId" class="text-sm flex gap-2 items-center">
                <span class="w-40 truncate">{{ ctx.appName(g.appId) }}</span>
                <div class="rounded h-4" :style="{ width: `${Math.max(2, g.seconds / (review.top5[0]?.seconds || 1) * 60)}%`, background: ctx.appColor(g.appId) }" />
                <span class="text-muted-foreground">{{ formatSessionDuration(g.seconds) }}</span>
              </div>
            </div>
          </div>
          <div class="card-shadow p-4 rounded-lg bg-card">
            <div class="text-sm font-medium mb-2">
              {{ t('report.monthlyDist') }}
            </div>
            <VChart :option="monthlyOption" autoresize class="h-56" />
          </div>
          <div class="card-shadow p-4 rounded-lg bg-card">
            <div class="text-sm font-medium mb-2">
              {{ t('report.calendar') }}
            </div>
            <VChart :option="calendarOption" autoresize class="h-56" />
          </div>
        </div>
      </template>

      <!-- 历史上的今天 -->
      <template v-else-if="activeTab === 'onthisday'">
        <el-date-picker v-model="thisDay" type="date" value-format="YYYY-MM-DD" class="mb-4" />
        <div v-if="thisDayRows.length === 0" class="text-muted-foreground py-12 text-center">
          {{ t('report.noThisDay') }}
        </div>
        <div v-for="row in thisDayRows" :key="row.year" class="card-shadow mb-4 p-4 rounded-lg bg-card">
          <div class="font-medium mb-2">
            {{ row.year }} · {{ formatSessionDuration(row.seconds) }}
          </div>
          <table class="text-sm w-full">
            <tbody>
              <tr v-for="(s, i) in row.sessions" :key="i" class="border-t">
                <td class="py-1">
                  {{ ctx.appName(s.appId) }}
                </td>
                <td>{{ dayjs.unix(s.startTime).format('HH:mm') }} – {{ dayjs.unix(s.endTime!).format('HH:mm') }}</td>
                <td class="text-right">
                  {{ formatSessionDuration(s.duration) }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </template>
    </FaPageMain>

    <!-- 年度幻灯片 -->
    <div v-if="slideshow" class="p-8 flex flex-col items-center inset-0 justify-center fixed z-50 from-primary/80 to-background bg-gradient-to-br" :class="{ 'transition-all duration-300': !reduceMotion }">
      <div class="mb-8 text-center">
        <div class="text-4xl font-bold">
          {{ slides[slide]?.title }}
        </div>
        <div class="text-2xl text-muted-foreground mt-4">
          {{ slides[slide]?.body }}
        </div>
        <div v-if="slides[slide]?.body2" class="text-lg text-muted-foreground mt-2">
          {{ slides[slide]?.body2 }}
        </div>
      </div>
      <div v-if="slides[slide]?.top5" class="max-w-lg w-full space-y-2">
        <div v-for="g in slides[slide].top5" :key="g.appId" class="text-lg flex gap-3 items-center">
          <span class="w-48 truncate">{{ ctx.appName(g.appId) }}</span>
          <div class="rounded h-6" :style="{ width: `${Math.max(3, g.seconds / (review.top5[0]?.seconds || 1) * 50)}%`, background: ctx.appColor(g.appId) }" />
          <span>{{ formatSessionDuration(g.seconds) }}</span>
        </div>
      </div>
      <div v-if="slides[slide]?.chart === 'monthly'" class="h-72 max-w-3xl w-full">
        <VChart :option="monthlyOption" autoresize class="h-full" />
      </div>
      <div v-if="slides[slide]?.chart === 'calendar'" class="h-56 max-w-3xl w-full">
        <VChart :option="calendarOption" autoresize class="h-full" />
      </div>
      <div class="mt-10 flex gap-4 items-center">
        <FaButton variant="ghost" size="icon-sm" :disabled="slide === 0" @click="slide--">
          <FaIcon name="i-mdi:chevron-left" />
        </FaButton>
        <div class="flex gap-1">
          <span v-for="(_, i) in slides" :key="i" class="rounded-full h-2 w-2" :class="i === slide ? 'bg-primary' : 'bg-muted'" />
        </div>
        <FaButton variant="ghost" size="icon-sm" :disabled="slide === slides.length - 1" @click="slide++">
          <FaIcon name="i-mdi:chevron-right" />
        </FaButton>
        <FaButton variant="outline" @click="slideshow = false">
          {{ t('common.close') }}
        </FaButton>
      </div>
    </div>

    <ShareImageDialog v-model="shareVisible" v-bind="shareProps" />
  </div>
</template>
