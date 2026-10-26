<script setup lang="ts">
import type { EChartsOption } from '@/utils/echarts'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { oklchToHex, parseOklch } from '@/features/stats/palette'
import { downloadCsv, downloadDataUrl, safeFileName } from '@/utils/download'
import '@/utils/echarts'

defineOptions({
  name: 'StatChartCard',
})

const props = withDefaults(defineProps<{
  title: string
  icon?: string
  description?: string
  option?: EChartsOption | null
  /** 图表高度（px） */
  height?: number
  loading?: boolean
  empty?: boolean
  emptyText?: string
  /** 导出 CSV 的行（第一行为表头） */
  csv?: () => unknown[][]
  /** 可在饼图 / 柱状图 / 矩形树图间切换 */
  types?: ('pie' | 'bar' | 'treemap')[]
}>(), {
  height: 320,
})

const emit = defineEmits<{
  chartClick: [params: any]
}>()

const chartType = defineModel<'pie' | 'bar' | 'treemap'>('type')

const { t } = useI18n()
const chartRef = ref<InstanceType<typeof VChart>>()
const fullscreen = ref(false)
const hasChart = computed(() => !!props.option && !props.empty)

const typeIcons: Record<'pie' | 'bar' | 'treemap', string> = {
  pie: 'i-mdi:chart-donut',
  bar: 'i-mdi:chart-bar',
  treemap: 'i-mdi:view-quilt-outline',
}

function exportPng() {
  const chart = chartRef.value
  if (!chart) {
    return
  }
  const background = parseOklch(getComputedStyle(document.documentElement).getPropertyValue('--card'))
  const url = chart.getDataURL({ type: 'png', pixelRatio: 2, backgroundColor: background ? oklchToHex(background) : '#fff' })
  downloadDataUrl(`${safeFileName(props.title)}.png`, url)
}

function exportCsv() {
  if (props.csv) {
    downloadCsv(`${safeFileName(props.title)}.csv`, props.csv())
  }
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    fullscreen.value = false
  }
}

watch(fullscreen, (value) => {
  if (value) {
    window.addEventListener('keydown', onKeydown)
  }
  else {
    window.removeEventListener('keydown', onKeydown)
  }
})

onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <div
    class="stat-card p-4 border rounded-lg bg-card flex flex-col"
    :class="fullscreen ? 'fixed inset-4 z-2000 shadow-2xl' : 'relative h-full'"
  >
    <div class="mb-2 flex gap-2 items-start justify-between">
      <div class="min-w-0">
        <div class="font-semibold flex gap-2 items-center">
          <FaIcon v-if="icon" :name="icon" class="text-primary shrink-0" />
          <span class="truncate">{{ title }}</span>
        </div>
        <div v-if="description" class="text-xs text-muted-foreground mt-0.5">
          {{ description }}
        </div>
      </div>
      <div class="flex shrink-0 flex-wrap gap-1 items-center justify-end">
        <slot name="actions" />
        <template v-if="types?.length">
          <FaTooltip v-for="type in types" :key="type" :text="t(`stats.chartType.${type}`)">
            <FaButton
              size="icon-sm"
              :variant="chartType === type ? 'secondary' : 'ghost'"
              @click="chartType = type"
            >
              <FaIcon :name="typeIcons[type]" />
            </FaButton>
          </FaTooltip>
        </template>
        <FaTooltip v-if="hasChart" :text="t('stats.exportPng')">
          <FaButton size="icon-sm" variant="ghost" @click="exportPng">
            <FaIcon name="i-mdi:image-outline" />
          </FaButton>
        </FaTooltip>
        <FaTooltip v-if="csv" :text="t('stats.exportCsv')">
          <FaButton size="icon-sm" variant="ghost" @click="exportCsv">
            <FaIcon name="i-mdi:file-delimited-outline" />
          </FaButton>
        </FaTooltip>
        <FaTooltip :text="fullscreen ? t('stats.exitFullscreen') : t('stats.fullscreen')">
          <FaButton size="icon-sm" variant="ghost" @click="fullscreen = !fullscreen">
            <FaIcon :name="fullscreen ? 'i-mdi:fullscreen-exit' : 'i-mdi:fullscreen'" />
          </FaButton>
        </FaTooltip>
      </div>
    </div>

    <div class="flex-1 min-h-0" :style="fullscreen ? undefined : { minHeight: `${height}px` }">
      <div v-if="loading" class="rounded-md bg-muted h-full w-full animate-pulse" :style="{ minHeight: `${height}px` }" />
      <slot v-else-if="$slots.default" />
      <div
        v-else-if="!hasChart"
        class="text-muted-foreground flex flex-col gap-2 h-full items-center justify-center"
        :style="{ minHeight: `${height}px` }"
      >
        <FaIcon name="i-mdi:chart-box-outline" class="opacity-40 size-12" />
        <span class="text-sm">{{ emptyText || t('stats.empty') }}</span>
        <slot name="empty-action" />
      </div>
      <VChart
        v-else
        ref="chartRef"
        :option="option!"
        autoresize
        class="w-full"
        :style="{ height: fullscreen ? '100%' : `${height}px` }"
        @click="emit('chartClick', $event)"
      />
    </div>
    <slot name="footer" />
  </div>
</template>

<style scoped>
.stat-card {
  transition: box-shadow 0.25s ease, transform 0.25s ease;
}

.stat-card:not(.fixed):hover {
  box-shadow: 0 10px 24px -12px rgb(0 0 0 / 25%);
  transform: translateY(-2px);
}

@media (prefers-reduced-motion: reduce) {
  .stat-card {
    transition: none;
  }

  .stat-card:not(.fixed):hover {
    transform: none;
  }
}
</style>
