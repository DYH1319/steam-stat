<script setup lang="ts">
import {
  BarChart,
  HeatmapChart,
  LineChart,
  PieChart,
  RadarChart,
} from 'echarts/charts'
import {
  GridComponent,
  LegendComponent,
  RadarComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { computed, onMounted, ref } from 'vue'
import VChart from 'vue-echarts'
import { toast } from 'vue-sonner'

// 注册 ECharts 组件
use([
  BarChart,
  HeatmapChart,
  LineChart,
  PieChart,
  RadarChart,
  GridComponent,
  LegendComponent,
  RadarComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
  CanvasRenderer,
])

const electronApi = (window as any).electron

const useAppRecords = ref<any[]>([])
const loading = ref(false)
const lastRefreshTime = ref<Date | null>(null)

// 获取应用使用记录
async function fetchUseAppRecords() {
  loading.value = true
  try {
    const res = await electronApi.steamGetValidUseAppRecord()
    useAppRecords.value = res.records
    console.warn(res.lastUpdateTime)
    lastRefreshTime.value = new Date(res.lastUpdateTime)
    console.warn(lastRefreshTime.value)
    toast.success('获取应用使用记录成功', {
      duration: 700,
    })
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

// 页面加载时自动获取数据
onMounted(() => {
  fetchUseAppRecords()
})

// 图表配置
const appDurationChartOption = computed(() => {
  if (!useAppRecords.value || useAppRecords.value.length === 0) {
    return null
  }

  const appDurationMap = new Map<number, { name: string, duration: number }>()
  useAppRecords.value.forEach((record: any) => {
    const appId = record.appId
    const appName = `${record.nameLocalized?.schinese ?? (record.nameLocalized?.sc_schinese ?? record.appName)} (${appId})` || `App ${appId}`
    const duration = record.duration
    const existing = appDurationMap.get(appId)
    if (existing) {
      existing.duration += duration
    }
    else {
      appDurationMap.set(appId, { name: appName, duration })
    }
  })

  const data = Array.from(appDurationMap.values())
    .map(item => ({
      name: item.name,
      value: item.duration,
    }))
    .sort((a, b) => b.value - a.value)
    .slice(0, 10)

  return {
    title: {
      text: '应用使用时长分布（TOP 10）',
      left: 'center',
      textStyle: {
        color: '#1A237E',
        fontSize: 18,
        fontWeight: 'bold',
      },
    },
    tooltip: {
      trigger: 'item',
      formatter: (params: any) => {
        const hours = Math.floor(params.value / 3600)
        const minutes = Math.floor((params.value % 3600) / 60)
        const seconds = params.value % 60
        return `${params.name}<br/>使用时长: ${hours}h ${minutes}m ${seconds}s<br/>占比: ${params.percent}%`
      },
    },
    legend: {
      orient: 'vertical',
      right: 10,
      top: 'middle',
      textStyle: {
        color: '#311B92',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: '使用时长',
        type: 'pie',
        radius: ['40%', '70%'],
        center: ['40%', '50%'],
        avoidLabelOverlap: false,
        itemStyle: {
          borderRadius: 10,
          borderColor: '#fff',
          borderWidth: 2,
          shadowBlur: 10,
          shadowOffsetX: 0,
          shadowOffsetY: 5,
          shadowColor: 'rgba(0, 0, 0, 0.2)',
          color: (params: any) => {
            const colorSets = [
              [{ offset: 0, color: '#667eea' }, { offset: 1, color: '#764ba2' }],
              [{ offset: 0, color: '#f093fb' }, { offset: 1, color: '#f5576c' }],
              [{ offset: 0, color: '#4facfe' }, { offset: 1, color: '#00f2fe' }],
              [{ offset: 0, color: '#43e97b' }, { offset: 1, color: '#38f9d7' }],
              [{ offset: 0, color: '#fa709a' }, { offset: 1, color: '#fee140' }],
              [{ offset: 0, color: '#ff9800' }, { offset: 1, color: '#ff5722' }],
              [{ offset: 0, color: '#00897B' }, { offset: 1, color: '#00f2fe' }],
              [{ offset: 0, color: '#00ff00' }, { offset: 1, color: '#00c000' }],
              [{ offset: 0, color: '#ffff00' }, { offset: 1, color: '#dddd22' }],
              [{ offset: 0, color: '#ff00ff' }, { offset: 1, color: '#dd22dd' }],
            ]
            return {
              type: 'linear',
              x: 0,
              y: 0,
              x2: 1,
              y2: 1,
              colorStops: colorSets[params.dataIndex % colorSets.length],
            }
          },
        },
        label: {
          show: false,
          position: 'center',
        },
        emphasis: {
          label: {
            show: true,
            fontSize: 20,
            fontWeight: 'bold',
          },
        },
        labelLine: {
          show: false,
        },
        data,
      },
    ],
  }
})

const dailyUsageChartOption = computed(() => {
  if (!useAppRecords.value || useAppRecords.value.length === 0) {
    return null
  }

  const dailyUsageMap = new Map<string, number>()
  useAppRecords.value.forEach((record: any) => {
    const date = new Date(record.startTime).toLocaleDateString()
    const duration = record.duration
    dailyUsageMap.set(date, (dailyUsageMap.get(date) || 0) + duration)
  })

  const dates = Array.from(dailyUsageMap.keys()).sort()
  const values = dates.map(date => dailyUsageMap.get(date)! / 3600)

  return {
    title: {
      text: '每日使用时长统计',
      left: 'center',
      textStyle: {
        color: '#00695C',
        fontSize: 18,
        fontWeight: 'bold',
      },
    },
    tooltip: {
      trigger: 'axis',
      formatter: (params: any) => {
        const hours = params[0].value.toFixed(2)
        return `${params[0].name}<br/>使用时长: ${hours} 小时`
      },
    },
    grid: {
      left: '3%',
      right: '4%',
      bottom: '3%',
      containLabel: true,
    },
    xAxis: {
      type: 'category',
      data: dates,
      axisLabel: {
        color: '#004D40',
        rotate: 45,
        fontWeight: 'bold',
      },
    },
    yAxis: {
      type: 'value',
      name: '小时',
      axisLabel: {
        color: '#004D40',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: '使用时长',
        type: 'bar',
        data: values,
        itemStyle: {
          color: {
            type: 'linear',
            x: 0,
            y: 0,
            x2: 0,
            y2: 1,
            colorStops: [
              { offset: 0, color: '#11998e' },
              { offset: 0.5, color: '#38ef7d' },
              { offset: 1, color: '#8BC34A' },
            ],
          },
          shadowBlur: 10,
          shadowColor: 'rgba(56, 239, 125, 0.5)',
          shadowOffsetY: 5,
          borderRadius: [5, 5, 0, 0],
        },
      },
    ],
  }
})

const appFrequencyChartOption = computed(() => {
  if (!useAppRecords.value || useAppRecords.value.length === 0) {
    return null
  }

  const appFrequencyMap = new Map<number, { name: string, count: number }>()
  useAppRecords.value.forEach((record: any) => {
    const appId = record.appId
    const appName = record.appName || `App ${appId}`
    const existing = appFrequencyMap.get(appId)
    if (existing) {
      existing.count += 1
    }
    else {
      appFrequencyMap.set(appId, { name: appName, count: 1 })
    }
  })

  const data = Array.from(appFrequencyMap.values())
    .sort((a, b) => b.count - a.count)
    .slice(0, 15)

  return {
    title: {
      text: '应用启动频率统计（TOP 15）',
      left: 'center',
      textStyle: {
        color: '#C62828',
        fontSize: 18,
        fontWeight: 'bold',
      },
    },
    tooltip: {
      trigger: 'axis',
      axisPointer: {
        type: 'shadow',
      },
    },
    grid: {
      left: '3%',
      right: '4%',
      bottom: '3%',
      containLabel: true,
    },
    xAxis: {
      type: 'value',
      axisLabel: {
        color: '#B71C1C',
        fontWeight: 'bold',
      },
    },
    yAxis: {
      type: 'category',
      data: data.map(d => d.name),
      axisLabel: {
        color: '#C62828',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: '启动次数',
        type: 'bar',
        data: data.map(d => d.count),
        itemStyle: {
          color: {
            type: 'linear',
            x: 0,
            y: 0,
            x2: 1,
            y2: 0,
            colorStops: [
              { offset: 0, color: '#F2709C' },
              { offset: 0.5, color: '#FF9472' },
              { offset: 1, color: '#FF6B6B' },
            ],
          },
          shadowBlur: 10,
          shadowColor: 'rgba(255, 107, 107, 0.6)',
          shadowOffsetX: 5,
          borderRadius: [0, 5, 5, 0],
        },
      },
    ],
  }
})

const usageTrendChartOption = computed(() => {
  if (!useAppRecords.value || useAppRecords.value.length === 0) {
    return null
  }

  const timelineData = useAppRecords.value
    .map((record: any) => ({
      time: new Date(record.startTime).toLocaleString(),
      duration: record.duration / 60,
      appName: `${record.appName} (${record.appId})` || `App ${record.appId}`,
    }))
    .sort((a: any, b: any) => new Date(a.time).getTime() - new Date(b.time).getTime())
    .slice(-50)

  return {
    title: {
      text: '使用时长趋势（最近50次）',
      left: 'center',
      textStyle: {
        color: '#F57C00',
        fontSize: 18,
        fontWeight: 'bold',
      },
    },
    tooltip: {
      trigger: 'axis',
      formatter: (params: any) => {
        const minutes = params[0].value.toFixed(2)
        const record = timelineData[params[0].dataIndex]
        return `${params[0].name}<br/>应用: ${record.appName}<br/>使用时长: ${minutes} 分钟`
      },
    },
    grid: {
      left: '3%',
      right: '4%',
      bottom: '3%',
      containLabel: true,
    },
    xAxis: {
      type: 'category',
      data: timelineData.map((d: any) => d.time),
      axisLabel: {
        color: '#E65100',
        rotate: 45,
        interval: Math.floor(timelineData.length / 10),
        fontWeight: 'bold',
      },
    },
    yAxis: {
      type: 'value',
      name: '分钟',
      axisLabel: {
        color: '#E65100',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: '使用时长',
        type: 'line',
        data: timelineData.map((d: any) => d.duration),
        smooth: true,
        lineStyle: {
          color: {
            type: 'linear',
            x: 0,
            y: 0,
            x2: 1,
            y2: 0,
            colorStops: [
              { offset: 0, color: '#F57C00' },
              { offset: 0.5, color: '#FF6F00' },
              { offset: 1, color: '#FF9800' },
            ],
          },
          width: 4,
          shadowBlur: 10,
          shadowColor: 'rgba(255, 152, 0, 0.5)',
        },
        areaStyle: {
          color: {
            type: 'linear',
            x: 0,
            y: 0,
            x2: 0,
            y2: 1,
            colorStops: [
              { offset: 0, color: 'rgba(245, 124, 0, 0.6)' },
              { offset: 0.5, color: 'rgba(255, 111, 0, 0.3)' },
              { offset: 1, color: 'rgba(255, 152, 0, 0.1)' },
            ],
          },
        },
      },
    ],
  }
})

// 统计摘要
const stats = computed(() => ({
  totalRecords: useAppRecords.value.length,
  uniqueApps: new Set(useAppRecords.value.map((r: any) => r.appId)).size,
  totalHours: (useAppRecords.value.reduce((sum: number, r: any) => sum + r.duration, 0) / 3600).toFixed(2),
  avgMinutes: (useAppRecords.value.reduce((sum: number, r: any) => sum + r.duration, 0) / useAppRecords.value.length / 60).toFixed(2),
}))
</script>

<template>
  <div>
    <FaPageHeader title="Steam 使用统计" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 头部信息 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-4 flex items-center justify-between">
              <div class="flex items-center gap-3">
                <span class="i-mdi:chart-box inline-block h-8 w-8 text-primary" />
                <div>
                  <h3 class="text-2xl font-bold">
                    应用使用记录统计
                  </h3>
                  <p class="text-sm text-gray-500">
                    全面分析您的 Steam 应用使用情况
                  </p>
                </div>
              </div>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime" class="text-xs text-gray-500">
                  上次刷新时间
                  <FaTooltip text="来自自动化获取运行应用脚本的上次获取信息的时间">
                    <FaIcon name="i-ri:question-line" />
                  </FaTooltip>
                  : {{ lastRefreshTime.toLocaleTimeString() }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading"
                  @click="fetchUseAppRecords"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  刷新数据
                </el-button>
              </div>
            </div>

            <!-- 统计摘要卡片 -->
            <div v-if="useAppRecords.length > 0" class="grid grid-cols-2 gap-4 lg:grid-cols-4">
              <div class="rounded-lg from-primary to-purple-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.totalRecords }}
                </div>
                <div class="text-sm opacity-90">
                  总记录数
                </div>
              </div>
              <div class="rounded-lg from-green-500 to-emerald-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.uniqueApps }}
                </div>
                <div class="text-sm opacity-90">
                  使用过的应用数
                </div>
              </div>
              <div class="rounded-lg from-orange-500 to-red-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.totalHours }}h
                </div>
                <div class="text-sm opacity-90">
                  总使用时长
                </div>
              </div>
              <div class="rounded-lg from-blue-500 to-cyan-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.avgMinutes }}min
                </div>
                <div class="text-sm opacity-90">
                  平均每次使用时长
                </div>
              </div>
            </div>
          </div>
        </Transition>

        <!-- 图表展示 -->
        <div v-if="useAppRecords.length > 0" v-loading="loading" class="grid grid-cols-1 gap-6 lg:grid-cols-2">
          <!-- 应用使用时长分布 -->
          <Transition name="chart-fade" appear>
            <div v-if="appDurationChartOption" class="rounded-lg bg-[var(--g-container-bg)] p-4 shadow-lg">
              <VChart :option="appDurationChartOption" class="h-96" autoresize />
            </div>
          </Transition>

          <!-- 每日使用时长统计 -->
          <Transition name="chart-fade" appear>
            <div v-if="dailyUsageChartOption" class="rounded-lg bg-[var(--g-container-bg)] p-4 shadow-lg">
              <VChart :option="dailyUsageChartOption" class="h-96" autoresize />
            </div>
          </Transition>

          <!-- 应用启动频率统计 -->
          <Transition name="chart-fade" appear>
            <div v-if="appFrequencyChartOption" class="rounded-lg bg-[var(--g-container-bg)] p-4 shadow-lg">
              <VChart :option="appFrequencyChartOption" class="h-96" autoresize />
            </div>
          </Transition>

          <!-- 使用时长趋势 -->
          <Transition name="chart-fade" appear>
            <div v-if="usageTrendChartOption" class="rounded-lg bg-[var(--g-container-bg)] p-4 shadow-lg">
              <VChart :option="usageTrendChartOption" class="h-96" autoresize />
            </div>
          </Transition>
        </div>

        <!-- 无数据提示 -->
        <div v-else-if="!loading" class="rounded-lg bg-[var(--g-container-bg)] p-12 shadow-lg">
          <el-empty description="暂无使用记录数据">
            <template #image>
              <span class="i-mdi:chart-box-outline inline-block h-20 w-20 text-gray-300" />
            </template>
          </el-empty>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
.slide-fade-enter-active,
.chart-fade-enter-active {
  transition: all 0.5s ease;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(-20px);
}

.chart-fade-enter-from {
  opacity: 0;
  transform: scale(0.95);
}
</style>
