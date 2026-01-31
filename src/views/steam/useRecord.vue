<script setup lang="ts">
import { BarChart, HeatmapChart, LineChart, PieChart, RadarChart } from 'echarts/charts'
import {
  GridComponent,
  LegendComponent,
  RadarComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components'
import * as echarts from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { computed, onMounted, ref } from 'vue'
import VChart from 'vue-echarts'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import dayjs from '@/dayjs'

echarts.use([
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

const { t } = useI18n()
const electronApi = (window as Window).electron

// electron 原始数据
const useAppRecords = ref<UseAppRecord[]>([])
const usersInRecords = ref<SteamUser[]>([])

const loading = ref(false)
const lastRefreshTime = ref<string>('')
const minDate = ref<number>(1759248000) // 2025-10-01T00:00:00+08:00
const maxDate = ref<number>(dayjs().unix()) // now()

// 筛选数据
const filters = ref<{
  selectedUsers?: string[]
  selectedData?: [string, string]
}>({})

onMounted(async () => {
  filters.value = JSON.parse(localStorage.getItem('filters') ?? '{}')
  await fetchUsersInRecords()
  await fetchUseAppRecords(false)
})

/**
 * 获取应用使用记录
 * @param showToast 是否显示获取成功提示
 */
async function fetchUseAppRecords(showToast = false) {
  loading.value = true
  try {
    // 存储筛选到 localStorage
    localStorage.setItem('filters', JSON.stringify(filters.value))

    const param = {
      steamIds: toRaw(filters.value.selectedUsers),
      startDate: dayjs(filters.value.selectedData?.at(0), 'YYYY-MM-DD').unix(),
      endDate: dayjs(filters.value.selectedData?.at(1), 'YYYY-MM-DD').unix() + 86400 - 1, // 偏移到结束日期的最后一秒 23:59:59
    }

    const res = await electronApi.steamGetValidUseAppRecord(param)
    useAppRecords.value = res.records
    lastRefreshTime.value = dayjs(res.lastUpdateTime).format('YYYY-MM-DD HH:mm:ss')

    // 如果有最小日期筛选，minDate 等于选择的最小日期
    if (filters.value.selectedData?.at(0)) {
      minDate.value = dayjs(filters.value.selectedData?.at(0), 'YYYY-MM-DD').unix()
    }
    // 根据返回数据设置最小日期限制（useAppRecords 已按照 startTime 升序排序）
    else if (res.records.length > 0) {
      minDate.value = res.records[0].startTime
    }

    // 如果有最大日期筛选，maxDate 等于选择的最大日期
    if (filters.value.selectedData?.at(1)) {
      maxDate.value = dayjs(filters.value.selectedData?.at(1), 'YYYY-MM-DD').unix()
    }
    else {
      maxDate.value = dayjs().unix()
    }

    if (showToast) {
      toast.success(t('useRecord.getSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

/**
 * 获取有记录的用户
 */
async function fetchUsersInRecords() {
  try {
    usersInRecords.value = await electronApi.steamGetUsersInRecord()
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

/**
 * 重置筛选条件
 */
async function resetFilters() {
  filters.value = {}
  localStorage.removeItem('filters')
  await fetchUsersInRecords()
  await fetchUseAppRecords(false)
}

/**
 * 统计摘要
 */
const stats = computed(() => ({
  totalRecords: useAppRecords.value.length,
  uniqueApps: new Set(useAppRecords.value.map(r => r.appId)).size,
  totalHours: (useAppRecords.value.reduce((sum, r) => sum + r.duration, 0) / 3600).toFixed(2),
  avgMinutes: (useAppRecords.value.reduce((sum, r) => sum + r.duration, 0) / 60 / useAppRecords.value.length).toFixed(2),
}))

/**
 * 应用使用时长分布图配置
 */
const appDurationChartOption = computed(() => {
  if (useAppRecords.value.length === 0) {
    return null
  }

  const appDurationMap = new Map<number, { name: string, duration: number }>()
  useAppRecords.value.forEach((record) => {
    const appId = record.appId
    const appName = `${record.appName}` || `App ${appId}`
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

  const color = [
    // '#37A2DA',
    // '#32C5E9',
    '#67E0E3',
    '#9FE6B8',
    '#FFDB5C',
    '#ff9f7f',
    '#fb7293',
    '#E062AE',
    '#E690D1',
    '#e7bcf3',
    '#9d96f5',
    // '#8378EA',
    '#96BFFF',
  ]

  return {
    title: {
      text: t('useRecord.appDurationChart'),
      textStyle: {
        color: '#8855b8',
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
        return `${params.name}<br/>${t('useRecord.usageTime')}: ${hours}h ${minutes}m ${seconds}s<br/>${t('useRecord.percentage')}: ${params.percent}%`
      },
    },
    legend: {
      orient: 'vertical',
      left: '60%',
      top: '23.5%',
      align: 'left',
      itemStyle: {
        borderWidth: 'inherit',
      },
      textStyle: {
        color,
        fontWeight: 'bold',
        overflow: 'truncate',
      },
      tooltip: {
        show: false,
      },
    },
    series: [
      {
        name: t('useRecord.usageTime'),
        type: 'pie',
        radius: ['40%', '70%'],
        center: ['30%', '55%'],
        avoidLabelOverlap: false,
        itemStyle: {
          borderRadius: 15,
          borderColor: '#aaa',
          borderWidth: 1,
          shadowBlur: 10,
          shadowOffsetX: 0,
          shadowOffsetY: 0,
          shadowColor: 'rgba(0, 0, 0, 0.2)',
        },
        color,
        label: {
          show: false,
          position: 'center',
        },
        emphasis: {
          label: {
            show: true,
            fontSize: 18,
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

/**
 * 每日使用时长趋势图配置
 */
const dailyUsageChartOption = computed(() => {
  if (useAppRecords.value.length === 0) {
    return null
  }

  const dailyUsageMap = new Map<string, number>()

  useAppRecords.value.forEach((record) => {
    const startTime = dayjs.unix(record.startTime)
    const endTime = dayjs.unix(record.endTime)

    // 生成跨越的所有日期
    let currentDate = startTime.startOf('day')

    while (currentDate.isBefore(endTime, 'second')) {
      // 计算当天的使用时长
      const dayStart = currentDate.startOf('day')
      const dayEnd = currentDate.endOf('day')

      // 取实际的开始和结束时间（在当天范围内）
      const actualStart = startTime.isAfter(dayStart) ? startTime : dayStart
      const actualEnd = endTime.isBefore(dayEnd) ? endTime : dayEnd

      const dayDuration = Math.max(0, actualEnd.diff(actualStart, 'second'))

      const dateStr = currentDate.format('YYYY-MM-DD')
      dailyUsageMap.set(dateStr, (dailyUsageMap.get(dateStr) || 0) + dayDuration)

      // 移动到下一天
      currentDate = currentDate.add(1, 'day')
    }
  })

  // 获取日期范围
  const minDateValue = dayjs.unix(minDate.value)
  const maxDateValue = dayjs.unix(maxDate.value)

  // 生成从 minDate 到 maxDate 的所有日期
  const dates: string[] = []
  let currentDate = minDateValue.startOf('day')
  const endDate = maxDateValue.startOf('day')

  while (currentDate.isBefore(endDate, 'day') || currentDate.isSame(endDate, 'day')) {
    dates.push(currentDate.format('YYYY-MM-DD'))
    currentDate = currentDate.add(1, 'day')
  }

  // 为每个日期生成数据，没有记录的日期设置为 0
  const values = dates.map(date => (dailyUsageMap.get(date) || 0) / 3600)

  return {
    title: {
      text: t('useRecord.dailyUsageChart'),
      left: 'center',
      textStyle: {
        color: '#03854f',
        fontSize: 18,
        fontWeight: 'bold',
      },
    },
    tooltip: {
      trigger: 'axis',
      formatter: (params: any) => {
        const hours = params[0].value.toFixed(2)
        return `${params[0].name}<br/>${t('useRecord.usageTime')}: ${hours} ${t('useRecord.hours')}`
      },
    },
    grid: {
      left: '0%',
      right: '0%',
      bottom: '0%',
    },
    xAxis: {
      type: 'category',
      data: dates,
      axisLabel: {
        color: '#03854f',
        rotate: 45,
        fontWeight: 'bold',
        fontSize: 12,
      },
    },
    yAxis: {
      type: 'value',
      name: t('useRecord.hours'),
      nameTextStyle: {
        color: '#03854f',
        fontWeight: 'bold',
      },
      axisLabel: {
        color: '#03854f',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: t('useRecord.usageTime'),
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
        animationDuration: 1000,
        animationDelay: (idx: any) => {
          // 越往后的数据延迟越大
          return idx * 10
        },
        animationEasing: 'elasticOut',
        animationDelayUpdate: (idx: any) => {
          return idx * 5
        },
      },
    ],
  }
})

/**
 * 应用启动频率统计图配置
 */
const appFrequencyChartOption = computed(() => {
  if (useAppRecords.value.length === 0) {
    return null
  }

  const appFrequencyMap = new Map<number, { name: string, count: number }>()
  useAppRecords.value.forEach((record) => {
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
      text: t('useRecord.launchCountChart'),
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
      left: '0%',
      right: '0%',
      bottom: '0%',
    },
    xAxis: {
      type: 'value',
      name: t('useRecord.times'),
      nameTextStyle: {
        color: '#B71C1C',
        fontWeight: 'bold',
      },
      axisLabel: {
        color: '#B71C1C',
        fontWeight: 'bold',
      },
    },
    yAxis: {
      type: 'category',
      inverse: true,
      data: data.map(d => d.name),
      axisLabel: {
        color: '#C62828',
        fontWeight: 'bold',
        width: 175,
        overflow: 'truncate',
      },
    },
    series: [
      {
        name: t('useRecord.launchCount'),
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
        animationDuration: 1000,
        animationDelay: (idx: any) => {
          // 越往后的数据延迟越大
          return idx * 50
        },
      },
    ],
  }
})

/**
 * 应用使用时长趋势图配置
 */
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
      text: t('useRecord.appUsageTrendChart'),
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
        return `${params[0].name}<br/>${t('useRecord.application')}: ${record.appName}<br/>${t('useRecord.usageTime')}: ${minutes} ${t('useRecord.minutes')}`
      },
    },
    grid: {
      left: '3%',
      right: '4%',
      bottom: '3%',
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
      name: t('useRecord.minutes'),
      axisLabel: {
        color: '#E65100',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: t('useRecord.usageTime'),
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
</script>

<template>
  <div>
    <FaPageMain>
      <div class="space-y-6">
        <!-- 头部信息 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg p-6 shadow-lg">
            <div class="mb-4 flex items-center justify-between">
              <div class="flex items-center gap-3">
                <span class="i-mdi:chart-box inline-block h-8 w-8 text-primary" />
                <div>
                  <h3 class="text-2xl font-bold">
                    {{ t('useRecord.title') }}
                  </h3>
                  <p class="text-sm text-gray-500">
                    {{ t('useRecord.subtitle') }}
                  </p>
                </div>
              </div>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime" class="text-xs text-gray-500">
                  {{ t('common.lastRefresh') }}
                  <FaTooltip :text="t('useRecord.lastUpdateTip')">
                    <FaIcon name="i-ri:question-line" />
                  </FaTooltip>
                  : {{ lastRefreshTime }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading"
                  @click="fetchUseAppRecords(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refreshData') }}
                </el-button>
              </div>
            </div>

            <!-- 筛选器 -->
            <div class="mb-4 flex flex-wrap items-center justify-center gap-4 rounded-lg from-purple-50 to-pink-50 bg-gradient-to-r p-4 dark:from-purple-900/20 dark:to-pink-900/20">
              <div class="flex items-center gap-2">
                <span class="i-mdi:account-multiple inline-block h-5 w-5 text-purple-600" />
                <span class="text-purple-700 font-semibold dark:text-purple-300">{{ t('useRecord.filterUserLabel') }}</span>
                <el-select
                  v-model="filters.selectedUsers"
                  multiple
                  :placeholder="t('useRecord.selectUser')"
                  style="width: 240px;"
                  collapse-tags
                  collapse-tags-tooltip
                  @change="fetchUseAppRecords(false)"
                >
                  <el-option
                    v-for="user in usersInRecords"
                    :key="user.steamIdStr"
                    :label="`${user.personaName} (${user.accountName})`"
                    :value="user.steamIdStr"
                  />
                </el-select>
              </div>

              <div class="flex items-center gap-2">
                <span class="i-mdi:calendar-range inline-block h-5 w-5 text-purple-600" />
                <span class="text-purple-700 font-semibold dark:text-purple-300">{{ t('useRecord.dateRangeLabel') }}</span>
                <el-date-picker
                  v-model="filters.selectedData"
                  type="daterange"
                  :range-separator="t('useRecord.rangeSeparator')"
                  :start-placeholder="t('useRecord.startDate')"
                  :end-placeholder="t('useRecord.endDate')"
                  value-format="YYYY-MM-DD"
                  @change="fetchUseAppRecords(false)"
                />
              </div>

              <el-button
                type="warning"
                plain
                @click="resetFilters"
              >
                <span class="i-mdi:restart mr-1 inline-block h-4 w-4" />
                {{ t('useRecord.resetFilter') }}
              </el-button>
            </div>

            <!-- 统计摘要卡片 -->
            <div v-if="useAppRecords.length > 0" class="grid grid-cols-2 gap-4 lg:grid-cols-4">
              <div
                class="rounded-lg from-purple-500 to-purple-600 bg-gradient-to-br p-4 text-white dark:from-purple-700 dark:to-purple-800 dark:text-gray-300"
              >
                <div class="text-3xl font-bold">
                  {{ stats.totalRecords }}
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.totalRecords') }}
                </div>
              </div>
              <div
                class="rounded-lg from-green-400 to-green-500 bg-gradient-to-br p-4 text-white dark:from-green-600 dark:to-green-700 dark:text-gray-300"
              >
                <div class="text-3xl font-bold">
                  {{ stats.uniqueApps }}
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.totalApps') }}
                </div>
              </div>
              <div
                class="rounded-lg from-orange-500 to-orange-600 bg-gradient-to-br p-4 text-white dark:from-orange-700 dark:to-orange-800 dark:text-gray-300"
              >
                <div class="text-3xl font-bold">
                  {{ stats.totalHours }}h
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.totalDuration') }}
                </div>
              </div>
              <div
                class="rounded-lg from-blue-500 to-blue-600 bg-gradient-to-br p-4 text-white dark:from-blue-700 dark:to-blue-800 dark:text-gray-300"
              >
                <div class="text-3xl font-bold">
                  {{ stats.avgMinutes }}min
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.avgTime') }}
                </div>
              </div>
            </div>
          </div>
        </Transition>

        <!-- 图表展示 -->
        <div v-if="useAppRecords.length > 0" v-loading="loading" class="grid grid-cols-[repeat(auto-fit,minmax(600px,1fr))] gap-6">
          <!-- 应用使用时长分布 -->
          <Transition name="chart-fade" appear>
            <div v-if="appDurationChartOption" class="rounded-lg p-4 shadow-lg">
              <VChart :option="appDurationChartOption" class="h-96" autoresize />
            </div>
          </Transition>

          <!-- 每日使用时长统计 -->
          <Transition name="chart-fade" appear>
            <div v-if="dailyUsageChartOption" class="rounded-lg p-4 shadow-lg">
              <VChart :option="dailyUsageChartOption" class="h-96" autoresize />
            </div>
          </Transition>

          <!-- 应用启动频率统计 -->
          <Transition name="chart-fade" appear>
            <div v-if="appFrequencyChartOption" class="rounded-lg p-4 shadow-lg">
              <VChart :option="appFrequencyChartOption" class="h-96" autoresize />
            </div>
          </Transition>

          <!-- 使用时长趋势 -->
          <Transition name="chart-fade" appear>
            <div v-if="usageTrendChartOption" class="rounded-lg p-4 shadow-lg">
              <VChart :option="usageTrendChartOption" class="h-96" autoresize />
            </div>
          </Transition>
        </div>

        <!-- 无数据提示 -->
        <div v-else-if="!loading" class="rounded-lg p-12 shadow-lg">
          <el-empty :description="t('useRecord.noRecords')">
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
