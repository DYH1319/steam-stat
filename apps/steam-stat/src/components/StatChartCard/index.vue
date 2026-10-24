<script setup lang="ts">
import type VChart from 'vue-echarts'
import { ref } from 'vue'
import { useChartTheme } from '@/composables/useChartTheme'
import '@/utils/echarts'

export interface StatChartCsv {
  headers: string[]
  rows: (string | number)[][]
}

const props = defineProps<{
  title: string
  icon?: string
  option?: object
  chartTypes?: ('pie' | 'bar' | 'treemap')[]
  loading?: boolean
  empty?: boolean
  emptyText?: string
  csv?: () => StatChartCsv
}>()

const emit = defineEmits<{ chartClick: [params: any] }>()
const chartType = defineModel<string>('chartType')
const { theme } = useChartTheme()
const chartRef = ref<InstanceType<typeof VChart>>()
const fullscreen = ref(false)

function onChartClick(params: any) {
  emit('chartClick', params)
}

function exportPng() {
  const dataUrl = (chartRef.value as any)?.getDataURL?.({ pixelRatio: 2, backgroundColor: theme.value.background })
  if (!dataUrl) {
    return
  }
  const anchor = document.createElement('a')
  anchor.href = dataUrl
  anchor.download = `${props.title}.png`
  anchor.click()
}

function exportCsv() {
  const data = props.csv?.()
  if (!data) {
    return
  }
  const quote = (v: string | number) => `"${String(v).replace(/"/g, '""')}"`
  const content = `\uFEFF${data.headers.map(quote).join(',')}\r\n${data.rows.map(r => r.map(quote).join(',')).join('\r\n')}`
  const blob = new Blob([content], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = `${props.title}.csv`
  anchor.click()
  URL.revokeObjectURL(url)
}
</script>

<template>
  <div class="card-shadow p-4 rounded-lg bg-card flex flex-col" :class="{ 'fixed inset-0 z-50 h-screen': fullscreen }">
    <div class="mb-2 flex flex-wrap gap-2 items-center">
      <FaIcon v-if="icon" :name="icon" class="text-lg" />
      <span class="text-sm font-medium">{{ title }}</span>
      <slot name="header" />
      <div class="ml-auto flex gap-1 items-center">
        <FaSelect
          v-if="chartTypes && chartTypes.length > 1"
          v-model="chartType"
          :options="chartTypes.map(t => ({ value: t, label: t }))"
          class="w-24"
        />
        <slot name="actions" />
        <FaButton v-if="csv" variant="ghost" size="icon-sm" title="CSV" @click="exportCsv">
          <FaIcon name="i-mdi:file-delimited-outline" />
        </FaButton>
        <FaButton v-if="option" variant="ghost" size="icon-sm" title="PNG" @click="exportPng">
          <FaIcon name="i-mdi:image-outline" />
        </FaButton>
        <FaButton variant="ghost" size="icon-sm" @click="fullscreen = !fullscreen">
          <FaIcon :name="fullscreen ? 'i-mdi:fullscreen-exit' : 'i-mdi:fullscreen'" />
        </FaButton>
      </div>
    </div>
    <div v-if="loading" class="rounded bg-muted flex-1 h-64 animate-pulse" />
    <div v-else-if="empty" class="text-muted-foreground flex flex-1 flex-col gap-2 h-64 items-center justify-center">
      <FaIcon name="i-mdi:chart-bar" class="opacity-40 h-16 w-16" />
      <span class="text-sm">{{ emptyText }}</span>
      <slot name="empty-action" />
    </div>
    <div v-else class="flex-1 min-h-64" :class="{ 'h-full': fullscreen }">
      <VChart v-if="option" ref="chartRef" :option="option" autoresize :class="fullscreen ? 'h-full' : 'h-64'" @click="onChartClick" />
      <slot v-else />
    </div>
  </div>
</template>
