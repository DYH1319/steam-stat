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
import { useI18n } from 'vue-i18n'
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

const { t } = useI18n()
const electronApi = (window as any).electron

const useAppRecords = ref<any[]>([])
const loginUsers = ref<any[]>([])
const loading = ref(false)
const lastRefreshTime = ref<Date | null>(null)

// 筛选条件
const selectedUserIds = ref<bigint[]>([])
const dateRange = ref<[string, string] | null>(null)
const minDate = ref<string>('')
const maxDate = ref<string>('')

// 获取登录用户
async function fetchLoginUsers() {
  try {
    const users = await electronApi.steamGetLoginUser()
    loginUsers.value = users
    // 默认选中所有用户
    selectedUserIds.value = users.map((u: any) => u.steamId.toString())
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

// 获取应用使用记录
async function fetchUseAppRecords(steamIds?: bigint[], startDate?: number, endDate?: number, showToast = false) {
  loading.value = true
  try {
    const res = await electronApi.steamGetValidUseAppRecord(steamIds, startDate, endDate)
    useAppRecords.value = res.records
    lastRefreshTime.value = new Date(res.lastUpdateTime)
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

// 应用筛选条件
function applyFilters(showToast = false) {
  const steamIds = selectedUserIds.value.length > 0 ? selectedUserIds.value : undefined
  const startDate = dateRange.value?.[0] ? new Date(`${dateRange.value[0]}T00:00:00`).getTime() : undefined
  const endDate = dateRange.value?.[1] ? new Date(`${dateRange.value[1]}T23:59:59`).getTime() : undefined
  fetchUseAppRecords(toRaw(steamIds), startDate, endDate, showToast)
}

// 重置筛选条件
function resetFilters() {
  // 重置为默认值：所有有记录的用户 + 全部日期范围
  const uniqueUserIds = Array.from(new Set(useAppRecords.value.map((r: any) => r.steamId.toString())))
  selectedUserIds.value = uniqueUserIds
  dateRange.value = minDate.value && maxDate.value ? [minDate.value, maxDate.value] : null
  applyFilters()
}

// 日期禁用函数：只允许选择 minDate 到 maxDate 范围内的日期
function disabledDate(time: Date) {
  if (!minDate.value || !maxDate.value) {
    return false
  }
  const min = new Date(minDate.value)
  min.setHours(0, 0, 0, 0)
  const max = new Date(maxDate.value)
  max.setHours(23, 59, 59, 999)
  return time.getTime() < min.getTime() || time.getTime() > max.getTime()
}

// 刷新数据
async function refreshData(showToast = false) {
  // 先无筛选获取所有记录
  await fetchUseAppRecords()
  await fetchLoginUsers()

  if (useAppRecords.value.length > 0) {
    // 计算有效记录的日期范围
    const dates = useAppRecords.value.map((r: any) => new Date(r.startTime).getTime())
    const minTime = Math.min(...dates)
    const maxTime = Date.now()

    // 设置日期范围限制
    minDate.value = new Date(minTime).toISOString().split('T')[0]
    maxDate.value = new Date(maxTime).toISOString().split('T')[0]

    // 设置默认筛选条件
    const uniqueUserIds = Array.from(new Set(useAppRecords.value.map((r: any) => r.steamId.toString())))
    selectedUserIds.value = uniqueUserIds
    dateRange.value = [minDate.value, maxDate.value]

    // 更新 loginUsers 只包含有记录的用户
    loginUsers.value = loginUsers.value.filter((u: any) => uniqueUserIds.includes(u.steamId.toString()))

    applyFilters(showToast)
  }
}

// 页面加载时自动获取数据
onMounted(async () => {
  await refreshData()
})

// 图表配置
const appDurationChartOption = computed(() => {
  if (!useAppRecords.value || useAppRecords.value.length === 0) {
    return null
  }

  const appDurationMap = new Map<number, { name: string, duration: number }>()
  useAppRecords.value.forEach((record: any) => {
    const appId = record.appId
    const appName = `${record.nameLocalized?.schinese ?? (record.nameLocalized?.sc_schinese ?? record.appName)}` || `App ${appId}`
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
      text: t('useRecord.appDurationChart'),
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
        return `${params.name}<br/>${t('useRecord.usageTime')}: ${hours}h ${minutes}m ${seconds}s<br/>${t('useRecord.percentage')}: ${params.percent}%`
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
        name: t('useRecord.usageTime'),
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
    media: [
      {
        // 小屏幕：宽度 < 800px
        query: {
          maxWidth: 750,
        },
        option: {
          series: [
            {
              radius: ['35%', '60%'],
              center: ['25%', '50%'],
            },
          ],
          legend: {
            right: '5%',
            itemWidth: 14,
            itemHeight: 14,
            textStyle: {
              fontSize: 12,
            },
          },
        },
      },
      {
        // 中屏幕：800px <= 宽度
        query: {
          minWidth: 750,
        },
        option: {
          series: [
            {
              radius: ['40%', '70%'],
              center: ['30%', '50%'],
            },
          ],
          legend: {
            right: '10%',
            itemWidth: 16,
            itemHeight: 16,
            textStyle: {
              fontSize: 12,
            },
          },
        },
      },
    ],
  }
})

const dailyUsageChartOption = computed(() => {
  if (!useAppRecords.value || useAppRecords.value.length === 0) {
    return null
  }

  const dailyUsageMap = new Map<string, number>()

  // 处理跨日期的游玩记录，按天拆分
  useAppRecords.value.forEach((record: any) => {
    const startTime = new Date(record.startTime)
    const endTime = new Date(record.endTime)
    const duration = record.duration

    // 如果开始和结束在同一天
    const startDateStr = startTime.toLocaleDateString()
    const endDateStr = endTime.toLocaleDateString()

    if (startDateStr === endDateStr) {
      dailyUsageMap.set(startDateStr, (dailyUsageMap.get(startDateStr) || 0) + duration)
    }
    else {
      // 跨天的记录需要拆分
      const currentDate = new Date(startTime)
      currentDate.setHours(0, 0, 0, 0)

      let remainingDuration = duration
      const endTimeValue = endTime.getTime()

      while (currentDate.getTime() <= endTimeValue) {
        const dateStr = currentDate.toLocaleDateString()
        const dayStart = new Date(currentDate)
        const dayEnd = new Date(currentDate)
        dayEnd.setHours(23, 59, 59, 999)

        let dayDuration: number

        if (currentDate.toLocaleDateString() === startTime.toLocaleDateString()) {
          // 第一天：从开始时间到当天结束
          dayDuration = Math.floor((dayEnd.getTime() - startTime.getTime()) / 1000)
        }
        else if (currentDate.toLocaleDateString() === endTime.toLocaleDateString()) {
          // 最后一天：从当天开始到结束时间
          dayDuration = Math.floor((endTime.getTime() - dayStart.getTime()) / 1000)
        }
        else {
          // 中间的天：整天24小时
          dayDuration = 86400
        }

        dayDuration = Math.min(dayDuration, remainingDuration)
        dailyUsageMap.set(dateStr, (dailyUsageMap.get(dateStr) || 0) + dayDuration)
        remainingDuration -= dayDuration

        // 移动到下一天
        currentDate.setDate(currentDate.getDate() + 1)
      }
    }
  })

  const dates = Array.from(dailyUsageMap.keys()).sort((a, b) => new Date(a).getTime() - new Date(b).getTime())
  const values = dates.map(date => dailyUsageMap.get(date)! / 3600)

  return {
    title: {
      text: t('useRecord.dailyUsageChart'),
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
        return `${params[0].name}<br/>${t('useRecord.usageTime')}: ${hours} ${t('useRecord.hours')}`
      },
    },
    grid: {
      left: '3%',
      right: '4%',
      bottom: '3%',
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
      name: t('useRecord.hours'),
      axisLabel: {
        color: '#004D40',
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
      left: '3%',
      right: '4%',
      bottom: '3%',
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
      },
    ],
    media: [
      {
        // 小屏幕：宽度 < 800px
        query: {
          maxWidth: 750,
        },
        option: {
          yAxis: {
            axisLabel: {
              fontSize: 11,
            },
          },
        },
      },
      {
        // 中屏幕：800px <= 宽度
        query: {
          minWidth: 750,
        },
        option: {
          yAxis: {
            axisLabel: {
              fontSize: 12,
            },
          },
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
                  : {{ lastRefreshTime.toLocaleTimeString() }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading"
                  @click="refreshData(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refreshData') }}
                </el-button>
              </div>
            </div>

            <!-- 筛选器 -->
            <Transition name="fade" appear>
              <div class="mb-4 flex flex-wrap items-center justify-center gap-4 rounded-lg from-purple-50 to-pink-50 bg-gradient-to-r p-4 dark:from-purple-900/20 dark:to-pink-900/20">
                <div class="flex items-center gap-2">
                  <span class="i-mdi:account-multiple inline-block h-5 w-5 text-purple-600" />
                  <span class="text-purple-700 font-semibold dark:text-purple-300">{{ t('useRecord.filterUserLabel') }}</span>
                  <el-select
                    v-model="selectedUserIds"
                    multiple
                    :placeholder="t('useRecord.selectUser')"
                    style="width: 240px;"
                    collapse-tags
                    collapse-tags-tooltip
                    @change="applyFilters"
                  >
                    <el-option
                      v-for="user in loginUsers"
                      :key="user.steamId"
                      :label="user.personaName"
                      :value="user.steamId.toString()"
                    />
                  </el-select>
                </div>

                <div class="flex items-center gap-2">
                  <span class="i-mdi:calendar-range inline-block h-5 w-5 text-purple-600" />
                  <span class="text-purple-700 font-semibold dark:text-purple-300">{{ t('useRecord.dateRangeLabel') }}</span>
                  <el-date-picker
                    v-model="dateRange"
                    type="daterange"
                    :range-separator="t('useRecord.rangeSeparator')"
                    :start-placeholder="t('useRecord.startDate')"
                    :end-placeholder="t('useRecord.endDate')"
                    value-format="YYYY-MM-DD"
                    :disabled-date="disabledDate"
                    @change="applyFilters"
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
            </Transition>

            <!-- 统计摘要卡片 -->
            <div v-if="useAppRecords.length > 0" class="grid grid-cols-2 gap-4 lg:grid-cols-4">
              <div class="rounded-lg from-primary to-purple-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.totalRecords }}
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.totalRecords') }}
                </div>
              </div>
              <div class="rounded-lg from-green-500 to-emerald-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.uniqueApps }}
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.totalApps') }}
                </div>
              </div>
              <div class="rounded-lg from-orange-500 to-red-500 bg-gradient-to-br p-4 text-white">
                <div class="text-3xl font-bold">
                  {{ stats.totalHours }}h
                </div>
                <div class="text-sm opacity-90">
                  {{ t('useRecord.totalDuration') }}
                </div>
              </div>
              <div class="rounded-lg from-blue-500 to-cyan-500 bg-gradient-to-br p-4 text-white">
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
