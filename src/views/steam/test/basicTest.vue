<route lang="yaml">
meta:
  title: Steam 测试
  icon: i-mdi:flask-outline
</route>

<script setup lang="ts">
import {
  BarChart,
  HeatmapChart,
  LineChart,
  PieChart,
  RadarChart,
  ScatterChart,
} from 'echarts/charts'
import {
  CalendarComponent,
  GridComponent,
  LegendComponent,
  RadarComponent,
  TitleComponent,
  ToolboxComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { computed, ref } from 'vue'
import VChart from 'vue-echarts'
import { toast } from 'vue-sonner'

// 注册 ECharts 组件
use([
  BarChart,
  HeatmapChart,
  LineChart,
  PieChart,
  RadarChart,
  ScatterChart,
  CalendarComponent,
  GridComponent,
  LegendComponent,
  RadarComponent,
  TitleComponent,
  ToolboxComponent,
  TooltipComponent,
  VisualMapComponent,
  CanvasRenderer,
])

const electronApi = (window as any).electron
const testResults = ref<Record<string, any>>({
  loginUser: null,
  steamStatus: null,
  runningApps: null,
  installedApps: null,
  libraryFolders: null,
  useAppRecords: null,
})
const loading = ref<Record<string, boolean>>({
  loginUser: false,
  steamStatus: false,
  runningApps: false,
  installedApps: false,
  libraryFolders: false,
  useAppRecords: false,
})

// 复制到剪贴板
function copyToClipboard(text: string) {
  navigator.clipboard.writeText(text)
  toast.success('已复制到剪贴板')
}
// 格式化字节大小
function formatBytes(bytes: bigint | number | null | undefined): string {
  if (!bytes) {
    return '0 B'
  }
  const value = typeof bytes === 'bigint' ? Number(bytes) : bytes
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const k = 1024
  const i = Math.floor(Math.log(value) / Math.log(k))
  return `${(value / k ** i).toFixed(2)} ${units[i]}`
}
// 1. 登录用户
async function doLoginUser() {
  loading.value.loginUser = true
  try {
    testResults.value.loginUser = await electronApi.steamTestGetLoginUser()
    toast.success('获取登录用户信息成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.loginUser = false
  }
}
function clearLoginUser() {
  testResults.value.loginUser = null
}
// 刷新登录用户
async function refreshLoginUser() {
  loading.value.loginUser = true
  try {
    testResults.value.loginUser = await electronApi.steamTestRefreshLoginUser()
    toast.success('刷新登录用户信息成功')
  }
  catch (e: any) {
    toast.error(`刷新失败: ${e?.message || e}`)
  }
  finally {
    loading.value.loginUser = false
  }
}
// 2. Steam状态
async function doSteamStatus() {
  loading.value.steamStatus = true
  try {
    testResults.value.steamStatus = await electronApi.steamTestGetStatus()
    toast.success('获取Steam状态成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.steamStatus = false
  }
}
function clearSteamStatus() {
  testResults.value.steamStatus = null
}
// 刷新Steam状态
async function refreshSteamStatus() {
  loading.value.steamStatus = true
  try {
    testResults.value.steamStatus = await electronApi.steamTestRefreshStatus()
    toast.success('刷新Steam状态成功')
  }
  catch (e: any) {
    toast.error(`刷新失败: ${e?.message || e}`)
  }
  finally {
    loading.value.steamStatus = false
  }
}
// 3. 运行中的应用
async function doRunningApps() {
  loading.value.runningApps = true
  try {
    testResults.value.runningApps = await electronApi.steamTestGetRunningApps()
    toast.success('获取运行中应用成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.runningApps = false
  }
}
function clearRunningApps() {
  testResults.value.runningApps = null
}
// 4. 已安装的应用
async function doInstalledApps() {
  loading.value.installedApps = true
  try {
    testResults.value.installedApps = await electronApi.steamTestGetInstalledApps()
    toast.success('获取已安装应用成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.installedApps = false
  }
}
function clearInstalledApps() {
  testResults.value.installedApps = null
}
// 5. 游戏库目录
async function doLibraryFolders() {
  loading.value.libraryFolders = true
  try {
    testResults.value.libraryFolders = await electronApi.steamTestGetLibraryFolders()
    toast.success('获取游戏库目录成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.libraryFolders = false
  }
}
function clearLibraryFolders() {
  testResults.value.libraryFolders = null
}
// 6. 应用使用记录统计
async function doUseAppRecords() {
  loading.value.useAppRecords = true
  try {
    testResults.value.useAppRecords = await electronApi.steamTestGetValidUseAppRecord()
    toast.success('获取应用使用记录成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.useAppRecords = false
  }
}
function clearUseAppRecords() {
  testResults.value.useAppRecords = null
}

// ECharts 配置
const appDurationChartOption = computed(() => {
  if (!testResults.value.useAppRecords || testResults.value.useAppRecords.length === 0) {
    return null
  }

  // 按应用统计总使用时长
  const appDurationMap = new Map<number, { name: string, duration: number }>()
  testResults.value.useAppRecords.forEach((record: any) => {
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
    .slice(0, 10) // 只显示前10个

  return {
    title: {
      text: '应用使用时长分布（TOP 10）',
      left: 'center',
      textStyle: {
        color: '#1A237E',
        fontSize: 18,
        fontWeight: 'bold',
      },
      subtext: '悬停查看详情',
      subtextStyle: {
        color: '#5E35B1',
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
            const colors = [
              { offset: 0, color: '#667eea' },
              { offset: 1, color: '#764ba2' },
            ]
            const colors2 = [
              { offset: 0, color: '#f093fb' },
              { offset: 1, color: '#f5576c' },
            ]
            const colors3 = [
              { offset: 0, color: '#4facfe' },
              { offset: 1, color: '#00f2fe' },
            ]
            const colors4 = [
              { offset: 0, color: '#43e97b' },
              { offset: 1, color: '#38f9d7' },
            ]
            const colors5 = [
              { offset: 0, color: '#fa709a' },
              { offset: 1, color: '#fee140' },
            ]
            const colors6 = [
              { offset: 0, color: '#ff9800' },
              { offset: 1, color: '#ff5722' },
            ]
            const colors7 = [
              { offset: 0, color: '#00897B' },
              { offset: 1, color: '#00f2fe' },
            ]
            const colors8 = [
              { offset: 0, color: '#00ff00' },
              { offset: 1, color: '#00c000' },
            ]
            const colors9 = [
              { offset: 0, color: '#ffff00' },
              { offset: 1, color: '#dddd22' },
            ]
            const colors10 = [
              { offset: 0, color: '#ff00ff' },
              { offset: 1, color: '#dd22dd' },
            ]
            const colorSets = [colors, colors2, colors3, colors4, colors5, colors6, colors7, colors8, colors9, colors10]
            const selectedColors = colorSets[params.dataIndex % colorSets.length]
            return {
              type: 'linear',
              x: 0,
              y: 0,
              x2: 1,
              y2: 1,
              colorStops: selectedColors,
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
  if (!testResults.value.useAppRecords || testResults.value.useAppRecords.length === 0) {
    return null
  }

  // 按日期统计使用时长
  const dailyUsageMap = new Map<string, number>()
  testResults.value.useAppRecords.forEach((record: any) => {
    const date = new Date(record.startTime).toLocaleDateString()
    const duration = record.duration
    dailyUsageMap.set(date, (dailyUsageMap.get(date) || 0) + duration)
  })

  const dates = Array.from(dailyUsageMap.keys()).sort()
  const values = dates.map(date => dailyUsageMap.get(date)! / 3600) // 转换为小时

  return {
    title: {
      text: '每日使用时长统计',
      left: 'center',
      textStyle: {
        color: '#00695C',
        fontSize: 18,
        fontWeight: 'bold',
      },
      subtext: '近期使用趋势',
      subtextStyle: {
        color: '#00897B',
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
      nameTextStyle: {
        color: '#00695C',
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
  if (!testResults.value.useAppRecords || testResults.value.useAppRecords.length === 0) {
    return null
  }

  // 按应用统计使用次数
  const appFrequencyMap = new Map<number, { name: string, count: number }>()
  testResults.value.useAppRecords.forEach((record: any) => {
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
      subtext: '最常用的应用',
      subtextStyle: {
        color: '#E53935',
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
  if (!testResults.value.useAppRecords || testResults.value.useAppRecords.length === 0) {
    return null
  }

  // 按时间顺序统计每次使用的时长
  const timelineData = testResults.value.useAppRecords
    .map((record: any) => ({
      time: new Date(record.startTime).toLocaleString(),
      duration: record.duration / 60, // 转换为分钟
      appId: record.appId,
      appName: `${record.appName} (${record.appId})` || `App ${record.appId}`,
    }))
    .sort((a: any, b: any) => new Date(a.time).getTime() - new Date(b.time).getTime())
    .slice(-50) // 只显示最近50条

  return {
    title: {
      text: '使用时长趋势（最近50次）',
      left: 'center',
      textStyle: {
        color: '#F57C00',
        fontSize: 18,
        fontWeight: 'bold',
      },
      subtext: '近期使用记录',
      subtextStyle: {
        color: '#FB8C00',
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
      nameTextStyle: {
        color: '#F57C00',
        fontWeight: 'bold',
      },
    },
    series: [
      {
        name: '使用时长',
        type: 'line',
        data: timelineData.map((d: any) => d.duration),
        smooth: true,
        itemStyle: {
          color: '#FF6F00',
          shadowBlur: 10,
          shadowColor: 'rgba(255, 111, 0, 0.5)',
        },
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
          shadowOffsetY: 5,
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

// 24小时活动热力图
const activityHeatmapChartOption = computed(() => {
  if (!testResults.value.useAppRecords || testResults.value.useAppRecords.length === 0) {
    return null
  }

  const now = new Date()
  const twentyFourHoursAgo = new Date(now.getTime() - 24 * 60 * 60 * 1000)

  // 过滤最近24小时的记录
  const recentRecords = testResults.value.useAppRecords.filter((record: any) => {
    const startTime = new Date(record.startTime)
    return startTime >= twentyFourHoursAgo && startTime <= new Date(Math.floor(now.getTime() / (10 * 60 * 1000)) * (10 * 60 * 1000))
  })

  if (recentRecords.length === 0) {
    return null
  }

  // 生成时间片（每10分钟一个）
  const timeSlots: string[] = []
  for (let i = 0; i < 144; i++) {
    const time = new Date(twentyFourHoursAgo.getTime() + i * 10 * 60 * 1000)
    timeSlots.push(`${time.getHours().toString().padStart(2, '0')}:${(Math.floor(time.getMinutes() / 10) * 10).toString().padStart(2, '0')}`)
  }

  // 获取所有应用
  const apps = Array.from(new Set(recentRecords.map((r: any) => r.appName || `App ${r.appId}`)))

  // 构建热力图数据 [timeIndex, appIndex, duration]
  const heatmapData: any[] = []
  recentRecords.forEach((record: any) => {
    const startTime = new Date(record.startTime)
    const endTime = record.endTime ? new Date(record.endTime) : now
    const appName = record.appName || `App ${record.appId}`
    const appIndex = apps.indexOf(appName)

    // 计算该记录覆盖的所有时间片
    let currentTime = new Date(startTime)
    while (currentTime < endTime) {
      const minutesSinceStart = Math.floor((currentTime.getTime() - twentyFourHoursAgo.getTime()) / (10 * 60 * 1000))
      if (minutesSinceStart >= 0 && minutesSinceStart < 144) {
        const timeSlot = `${currentTime.getHours().toString().padStart(2, '0')}:${(Math.floor(currentTime.getMinutes() / 10) * 10).toString().padStart(2, '0')}`
        const timeIndex = timeSlots.indexOf(timeSlot)
        if (timeIndex >= 0) {
          // 计算这个时间片内的使用时长（秒）
          const slotEnd = new Date(Math.floor(currentTime.getTime() / (10 * 60 * 1000)) * (10 * 60 * 1000) + 10 * 60 * 1000)
          const duration = Math.min(slotEnd.getTime(), endTime.getTime()) - currentTime.getTime()
          const durationMinutes = duration / 1000 / 60

          // 查找是否已有该位置的数据
          const existing = heatmapData.find(d => d[0] === timeIndex && d[1] === appIndex)
          if (existing) {
            existing[2] += durationMinutes
          }
          else {
            heatmapData.push([timeIndex, appIndex, durationMinutes])
          }
        }
      }
      currentTime = new Date(Math.floor(currentTime.getTime() / (10 * 60 * 1000)) * (10 * 60 * 1000) + 10 * 60 * 1000)
    }
  })

  // 每隔一定间隔显示时间标签
  const showEveryNth = Math.ceil(timeSlots.length / 24)

  return {
    title: {
      text: '24小时应用活动热力图',
      left: 'center',
      textStyle: {
        color: '#0D47A1',
        fontSize: 18,
        fontWeight: 'bold',
      },
    },
    tooltip: {
      position: 'top',
      formatter: (params: any) => {
        const timeSlot = timeSlots[params.data[0]]
        const appName = apps[params.data[1]]
        const duration = params.data[2].toFixed(1)
        return `${timeSlot}<br/>${appName}<br/>使用时长: ${duration} 分钟`
      },
    },
    grid: {
      left: '10%',
      right: '5%',
      top: '15%',
      bottom: '20%',
      containLabel: false,
    },
    xAxis: {
      type: 'category',
      nameLocation: 'middle',
      nameGap: 30,
      nameTextStyle: {
        color: '#0D47A1',
        fontSize: 13,
        fontWeight: 'bold',
      },
      data: timeSlots,
      splitArea: {
        show: true,
      },
      axisLabel: {
        color: '#1565C0',
        interval: showEveryNth - 1,
        rotate: 45,
        fontSize: 10,
      },
      axisTick: {
        show: false,
      },
    },
    yAxis: {
      type: 'category',
      nameLocation: 'middle',
      nameGap: 80,
      nameTextStyle: {
        color: '#0D47A1',
        fontSize: 13,
        fontWeight: 'bold',
      },
      data: apps,
      splitArea: {
        show: true,
      },
      axisLabel: {
        color: '#1565C0',
        fontSize: 11,
      },
      axisTick: {
        show: false,
      },
    },
    visualMap: {
      min: 0,
      max: 10,
      calculable: false,
      orient: 'horizontal',
      left: 'center',
      bottom: '0%',
      text: ['高强度(10分钟)', '低强度(0分钟)'],
      inRange: {
        color: ['#00FF00', '#55FF00', '#AAFF00', '#FFFF00', '#FFE100', '#FFC300', '#FFA500', '#FF6E00', '#FF3700', '#FF0000'],
      },
      textStyle: {
        color: '#1A237E',
        fontWeight: 'bold',
      },
      formatter: (value: number) => `${value.toFixed(1)}分钟`,
    },
    series: [
      {
        name: '使用时长',
        type: 'heatmap',
        data: heatmapData,
        emphasis: {
          itemStyle: {
            shadowBlur: 10,
            shadowColor: 'rgba(0, 0, 0, 0.5)',
          },
        },
      },
    ],
  }
})

// 应用使用时段分布雷达图
const timeDistributionRadarChartOption = computed(() => {
  if (!testResults.value.useAppRecords || testResults.value.useAppRecords.length === 0) {
    return null
  }

  // 定义时段
  const timePeriods = {
    '凌晨 00-06': { start: 0, end: 6, label: '凌晨(00:00-06:00)' },
    '早晨 06-09': { start: 6, end: 9, label: '早晨(06:00-09:00)' },
    '上午 09-12': { start: 9, end: 12, label: '上午(09:00-12:00)' },
    '中午 12-14': { start: 12, end: 14, label: '中午(12:00-14:00)' },
    '下午 14-18': { start: 14, end: 18, label: '下午(14:00-18:00)' },
    '傍晚 18-20': { start: 18, end: 20, label: '傍晚(18:00-20:00)' },
    '晚上 20-24': { start: 20, end: 24, label: '晚上(20:00-24:00)' },
  }

  // 按时段统计使用时长和次数
  const periodStats: Record<string, { duration: number, count: number }> = {}
  Object.keys(timePeriods).forEach((period) => {
    periodStats[period] = { duration: 0, count: 0 }
  })

  testResults.value.useAppRecords.forEach((record: any) => {
    const startTime = new Date(record.startTime)
    const hour = startTime.getHours()
    const duration = record.duration / 60 // 转换为分钟

    for (const [period, range] of Object.entries(timePeriods)) {
      if (hour >= range.start && hour < range.end) {
        periodStats[period].duration += duration
        periodStats[period].count += 1
        break
      }
    }
  })

  // 计算最大值用于雷达图比例
  const maxDuration = Math.max(...Object.values(periodStats).map(s => s.duration))
  const maxCount = Math.max(...Object.values(periodStats).map(s => s.count))

  return {
    title: {
      text: '应用使用时段分布分析',
      subtext: '蓝色=使用时长(分钟) | 绿色=启动次数(次)',
      left: 'center',
      textStyle: {
        color: '#6A1B9A',
        fontSize: 18,
        fontWeight: 'bold',
      },
      subtextStyle: {
        color: '#8E24AA',
        fontSize: 12,
      },
    },
    tooltip: {
      trigger: 'item',
      confine: true,
      formatter: (params: any) => {
        if (!params.value || params.value.length === 0) {
          return ''
        }
        const periodKeys = Object.keys(timePeriods)
        let result = `<strong>${params.seriesName}</strong><br/>`
        periodKeys.forEach((periodKey, idx) => {
          const key = periodKey as keyof typeof timePeriods
          const periodName = timePeriods[key].label
          const val = params.value[idx]
          if (params.seriesName === '使用时长') {
            result += `${periodName}: <strong>${val.toFixed(2)}</strong> 分钟<br/>`
          }
          else {
            result += `${periodName}: <strong>${val}</strong> 次<br/>`
          }
        })
        return result
      },
    },
    legend: {
      data: ['使用时长 (分钟)', '启动次数 (次)'],
      bottom: 10,
      textStyle: {
        color: '#4A148C',
        fontWeight: 'bold',
        fontSize: 13,
      },
    },
    radar: [
      {
        indicator: Object.keys(timePeriods).map(period => ({
          name: period,
          max: maxDuration > 0 ? maxDuration * 1.2 : 100,
        })),
        center: ['25%', '55%'],
        radius: '60%',
        shape: 'polygon',
        splitNumber: 4,
        name: {
          textStyle: {
            color: '#1565C0',
            fontSize: 14,
            fontWeight: 'bold',
          },
        },
        splitLine: {
          lineStyle: {
            color: 'rgba(33, 150, 243, 0.5)',
            width: 2,
          },
        },
        splitArea: {
          show: true,
          areaStyle: {
            color: ['rgba(33, 150, 243, 0.08)', 'rgba(33, 150, 243, 0.15)'],
          },
        },
        axisLine: {
          lineStyle: {
            color: 'rgba(33, 150, 243, 0.8)',
            width: 2,
          },
        },
      },
      {
        indicator: Object.keys(timePeriods).map(period => ({
          name: period,
          max: maxCount > 0 ? maxCount * 1.2 : 10,
        })),
        center: ['75%', '55%'],
        radius: '60%',
        shape: 'polygon',
        splitNumber: 4,
        name: {
          textStyle: {
            color: '#2E7D32',
            fontSize: 14,
            fontWeight: 'bold',
          },
        },
        splitLine: {
          lineStyle: {
            color: 'rgba(76, 175, 80, 0.5)',
            width: 2,
          },
        },
        splitArea: {
          show: true,
          areaStyle: {
            color: ['rgba(76, 175, 80, 0.08)', 'rgba(76, 175, 80, 0.15)'],
          },
        },
        axisLine: {
          lineStyle: {
            color: 'rgba(76, 175, 80, 0.8)',
            width: 2,
          },
        },
      },
    ],
    series: [
      {
        name: '使用时长',
        type: 'radar',
        radarIndex: 0,
        symbol: 'circle',
        symbolSize: 8,
        data: [
          {
            value: Object.keys(timePeriods).map(period => periodStats[period].duration),
            name: '使用时长 (分钟)',
            areaStyle: {
              color: {
                type: 'radial',
                x: 0.5,
                y: 0.5,
                r: 0.5,
                colorStops: [
                  { offset: 0, color: 'rgba(33, 150, 243, 0.7)' },
                  { offset: 0.5, color: 'rgba(33, 150, 243, 0.4)' },
                  { offset: 1, color: 'rgba(33, 150, 243, 0.1)' },
                ],
              },
              shadowBlur: 20,
              shadowColor: 'rgba(33, 150, 243, 0.5)',
            },
            lineStyle: {
              color: '#2196F3',
              width: 3,
              shadowBlur: 10,
              shadowColor: 'rgba(33, 150, 243, 0.8)',
            },
            itemStyle: {
              color: '#2196F3',
              borderColor: '#fff',
              borderWidth: 3,
              shadowBlur: 10,
              shadowColor: 'rgba(33, 150, 243, 0.6)',
            },
          },
        ],
      },
      {
        name: '启动次数',
        type: 'radar',
        radarIndex: 1,
        symbol: 'circle',
        symbolSize: 8,
        data: [
          {
            value: Object.keys(timePeriods).map(period => periodStats[period].count),
            name: '启动次数 (次)',
            areaStyle: {
              color: {
                type: 'radial',
                x: 0.5,
                y: 0.5,
                r: 0.5,
                colorStops: [
                  { offset: 0, color: 'rgba(76, 175, 80, 0.7)' },
                  { offset: 0.5, color: 'rgba(76, 175, 80, 0.4)' },
                  { offset: 1, color: 'rgba(76, 175, 80, 0.1)' },
                ],
              },
              shadowBlur: 20,
              shadowColor: 'rgba(76, 175, 80, 0.5)',
            },
            lineStyle: {
              color: '#4CAF50',
              width: 3,
              shadowBlur: 10,
              shadowColor: 'rgba(76, 175, 80, 0.8)',
            },
            itemStyle: {
              color: '#4CAF50',
              borderColor: '#fff',
              borderWidth: 3,
              shadowBlur: 10,
              shadowColor: 'rgba(76, 175, 80, 0.6)',
            },
          },
        ],
      },
    ],
  }
})
</script>

<template>
  <div>
    <FaPageHeader title="Steam 测试（新）" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 1. 登录用户信息 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              1. 获取Steam本机登录用户信息
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.loginUser" @click="doLoginUser">
                执行测试
              </el-button>
              <el-button type="success" :loading="loading.loginUser" :disabled="!testResults.loginUser" @click="refreshLoginUser">
                <span class="i-mdi:refresh inline-block h-4 w-4" />
                刷新数据
              </el-button>
              <el-button :disabled="!testResults.loginUser" @click="clearLoginUser">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.loginUser" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <template v-if="Array.isArray(testResults.loginUser) && testResults.loginUser.length > 0">
              <el-tag size="large" type="success" class="mb-4">
                共 {{ testResults.loginUser.length }} 个已登录的用户
              </el-tag>
              <div class="grid grid-cols-1 gap-4 lg:grid-cols-2 xl:grid-cols-3">
                <div
                  v-for="user in testResults.loginUser"
                  :key="user.steamId"
                  class="border rounded-lg bg-white p-4 shadow-sm transition-all dark:bg-gray-900 hover:shadow-md"
                >
                  <div class="mb-3 flex items-center justify-between">
                    <div class="flex items-center gap-2">
                      <span class="i-mdi:account-circle inline-block h-8 w-8 text-primary" />
                      <div>
                        <div class="text-lg font-semibold">
                          {{ user.personaName || user.accountName }}
                        </div>
                        <div class="text-sm text-gray-500">
                          @{{ user.accountName }}
                        </div>
                      </div>
                    </div>
                    <el-tag v-if="user.rememberPassword" type="success" size="small">
                      <span class="i-mdi:lock-check inline-block h-3 w-3" /> 已保存密码
                    </el-tag>
                  </div>
                  <el-descriptions :column="1" size="small" border>
                    <el-descriptions-item label="Steam ID">
                      <span class="text-xs font-mono">{{ user.steamId?.toString() }}</span>
                    </el-descriptions-item>
                    <el-descriptions-item label="Account ID">
                      <span class="text-xs font-mono">{{ user.accountId }}</span>
                    </el-descriptions-item>
                    <el-descriptions-item label="更新时间">
                      <span class="text-xs">{{ new Date(user.refreshTime).toLocaleString() }}</span>
                    </el-descriptions-item>
                  </el-descriptions>
                </div>
              </div>
            </template>
            <template v-else>
              <el-empty description="暂无用户数据" />
            </template>
          </div>
        </div>
        <!-- Steam状态 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              2. 获取Steam状态
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.steamStatus" @click="doSteamStatus">
                执行测试
              </el-button>
              <el-button type="success" :loading="loading.steamStatus" :disabled="!testResults.steamStatus" @click="refreshSteamStatus">
                <span class="i-mdi:refresh inline-block h-4 w-4" />
                刷新数据
              </el-button>
              <el-button :disabled="!testResults.steamStatus" @click="clearSteamStatus">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.steamStatus" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-4 flex flex-wrap items-center gap-2">
              <el-tag
                size="large"
                :type="Number(testResults.steamStatus.steamPid) > 0 ? 'success' : 'info'"
              >
                <span class="i-mdi:steam inline-block h-4 w-4" />
                {{ Number(testResults.steamStatus.steamPid) > 0 ? 'Steam 正在运行' : 'Steam 未运行' }}
                <span v-if="Number(testResults.steamStatus.steamPid) > 0"> (PID: {{ testResults.steamStatus.steamPid }})</span>
              </el-tag>
              <el-tag
                size="large"
                :type="Number(testResults.steamStatus.activeUserSteamId) > 0 ? 'primary' : 'info'"
              >
                <span class="i-mdi:account inline-block h-4 w-4" />
                {{ Number(testResults.steamStatus.activeUserSteamId) > 0 ? '用户' : '无登录用户' }}
                <span v-if="Number(testResults.steamStatus.activeUserSteamId) > 0"> (Steam ID: {{ testResults.steamStatus.activeUserSteamId }})</span>
              </el-tag>
              <el-tag
                size="large"
                :type="Number(testResults.steamStatus.runningAppId) > 0 ? 'warning' : 'info'"
              >
                <span class="i-mdi:gamepad inline-block h-4 w-4" />
                {{ Number(testResults.steamStatus.runningAppId) > 0 ? 'Steam对外展示的正在运行的应用 ID' : '无运行应用' }}
                <span v-if="Number(testResults.steamStatus.runningAppId) > 0"> (APP ID: {{ testResults.steamStatus.runningAppId }})</span>
              </el-tag>
              <el-tag v-if="testResults.steamStatus.refreshTime" size="large" type="info">
                <span class="i-mdi:clock inline-block h-4 w-4" />
                数据更新时间: {{ new Date(testResults.steamStatus.refreshTime).toLocaleString() }}
              </el-tag>
            </div>
            <div class="space-y-3">
              <!-- 安装路径 -->
              <div v-if="testResults.steamStatus.steamPath" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:folder inline-block h-5 w-5 text-blue-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">安装路径</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamPath }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamPath)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- 可执行文件 -->
              <div v-if="testResults.steamStatus.steamExePath" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:application inline-block h-5 w-5 text-green-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">可执行文件</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamExePath }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamExePath)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- SteamClient DLL -->
              <div v-if="testResults.steamStatus.steamClientDllPath" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:file-code inline-block h-5 w-5 text-purple-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">SteamClient DLL (32位)</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamClientDllPath }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamClientDllPath)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- SteamClient DLL 64位 -->
              <div v-if="testResults.steamStatus.steamClientDll64Path" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:file-code inline-block h-5 w-5 text-orange-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">SteamClient DLL (64位)</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamClientDll64Path }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamClientDll64Path)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- 无数据提示 -->
              <div v-if="!testResults.steamStatus.steamPath && !testResults.steamStatus.steamExePath && !testResults.steamStatus.steamClientDllPath && !testResults.steamStatus.steamClientDll64Path">
                <el-empty description="暂无路径信息" :image-size="80" />
              </div>
            </div>
          </div>
        </div>
        <!-- 运行中应用 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              3. 获取运行中的Steam应用
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.runningApps" @click="doRunningApps">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.runningApps" @click="clearRunningApps">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.runningApps" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" :type="testResults.runningApps.length > 0 ? 'success' : 'info'">
              <span class="i-mdi:gamepad-variant inline-block h-4 w-4" />
              {{ testResults.runningApps.length > 0 ? `${testResults.runningApps.length} 个应用运行中` : '当前无运行应用' }}
            </el-tag>
            <div v-if="testResults.runningApps.length > 0" class="grid grid-cols-1 gap-3 lg:grid-cols-3 md:grid-cols-2">
              <div
                v-for="app in testResults.runningApps"
                :key="app.appId"
                class="border rounded-lg bg-white p-3 shadow-sm dark:bg-gray-900"
              >
                <div class="flex items-center gap-2">
                  <span class="i-mdi:gamepad-variant text-success inline-block h-6 w-6" />
                  <div class="flex-1">
                    <div class="font-semibold">
                      {{ app.name }}
                    </div>
                    <div class="text-xs text-gray-500">
                      App ID: {{ app.appId }}
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
        <!-- 已安装应用 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              4. 获取已安装的Steam应用
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.installedApps" @click="doInstalledApps">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.installedApps" @click="clearInstalledApps">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.installedApps" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" type="success">
              <span class="i-mdi:folder-download inline-block h-4 w-4" />
              共 {{ testResults.installedApps.length }} 个已安装应用
            </el-tag>
            <el-table :data="testResults.installedApps" stripe max-height="500">
              <el-table-column prop="appId" label="App ID" width="100" sortable fixed />
              <el-table-column prop="name" label="应用名称" min-width="200" sortable show-overflow-tooltip>
                <template #default="{ row }">
                  <div class="flex items-center gap-2">
                    <el-tag v-if="row.isRunning" type="success" size="small" effect="dark">
                      <span class="i-mdi:play inline-block h-3 w-3" />
                      运行中
                    </el-tag>
                    <span>{{ row.name || '-' }}</span>
                  </div>
                </template>
              </el-table-column>
              <el-table-column label="本地化名称" min-width="120" show-overflow-tooltip>
                <template #default="{ row }">
                  <span v-if="row.nameLocalized && row.nameLocalized.length > 0" class="text-sm text-gray-600 dark:text-gray-400">
                    {{ row.nameLocalized.join(', ') }}
                  </span>
                  <span v-else class="text-sm text-gray-400">-</span>
                </template>
              </el-table-column>
              <el-table-column prop="installDir" label="安装目录名" min-width="150" sortable show-overflow-tooltip />
              <el-table-column label="安装路径" min-width="200" show-overflow-tooltip>
                <template #default="{ row }">
                  <span class="text-xs font-mono">{{ row.installDirPath || '-' }}</span>
                </template>
              </el-table-column>
              <el-table-column label="占用空间" width="130" sortable align="right">
                <template #default="{ row }">
                  <div class="flex flex-col items-end">
                    <span v-if="row.appOnDisk" class="text-xs font-mono">
                      {{ formatBytes(row.appOnDisk) }}
                    </span>
                    <span v-if="row.appOnDiskReal && row.appOnDiskReal !== row.appOnDisk" class="text-xs text-gray-500 font-mono">
                      实际: {{ formatBytes(row.appOnDiskReal) }}
                    </span>
                  </div>
                </template>
              </el-table-column>
              <el-table-column label="状态" width="90" align="center">
                <template #default="{ row }">
                  <el-tag :type="row.installed ? 'success' : 'info'" size="small">
                    {{ row.installed ? '已安装' : '未安装' }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column label="更新时间" width="160" sortable>
                <template #default="{ row }">
                  <span class="text-xs">{{ row.refreshTime ? new Date(row.refreshTime).toLocaleString() : '-' }}</span>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
        <!-- 游戏库目录 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              5. 获取Steam库目录
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.libraryFolders" @click="doLibraryFolders">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.libraryFolders" @click="clearLibraryFolders">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.libraryFolders && testResults.libraryFolders.length" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" type="success">
              <span class="i-mdi:folder-multiple inline-block h-4 w-4" />
              共 {{ testResults.libraryFolders.length }} 个库目录
            </el-tag>
            <div class="space-y-2">
              <div
                v-for="(folder, idx) in testResults.libraryFolders"
                :key="folder"
                class="flex items-center gap-2 border rounded-lg bg-white p-3 dark:bg-gray-900"
              >
                <span class="i-mdi:folder inline-block h-5 w-5 text-primary" />
                <span class="text-gray-600 font-medium dark:text-gray-400">库 {{ idx + 1 }}:</span>
                <span class="flex-1 text-sm font-mono">{{ folder }}</span>
              </div>
            </div>
          </div>
          <div v-else-if="testResults.libraryFolders" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-empty description="暂无库目录数据" />
          </div>
        </div>
        <!-- 应用使用记录统计 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              6. 应用使用记录统计
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.useAppRecords" @click="doUseAppRecords">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.useAppRecords" @click="clearUseAppRecords">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.useAppRecords" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" type="success">
              <span class="i-mdi:chart-box inline-block h-4 w-4" />
              共 {{ testResults.useAppRecords.length }} 条有效使用记录
            </el-tag>

            <div v-if="testResults.useAppRecords.length > 0" class="space-y-6">
              <!-- 图表网格 -->
              <div class="grid grid-cols-1 gap-6 lg:grid-cols-2">
                <!-- 应用使用时长分布 -->
                <div v-if="appDurationChartOption" class="border rounded-lg bg-white p-4 shadow-sm dark:bg-gray-900">
                  <VChart :option="appDurationChartOption" class="h-96" autoresize />
                </div>

                <!-- 每日使用时长统计 -->
                <div v-if="dailyUsageChartOption" class="border rounded-lg bg-white p-4 shadow-sm dark:bg-gray-900">
                  <VChart :option="dailyUsageChartOption" class="h-96" autoresize />
                </div>

                <!-- 应用启动频率统计 -->
                <div v-if="appFrequencyChartOption" class="border rounded-lg bg-white p-4 shadow-sm dark:bg-gray-900">
                  <VChart :option="appFrequencyChartOption" class="h-96" autoresize />
                </div>

                <!-- 使用时长趋势 -->
                <div v-if="usageTrendChartOption" class="border rounded-lg bg-white p-4 shadow-sm dark:bg-gray-900">
                  <VChart :option="usageTrendChartOption" class="h-96" autoresize />
                </div>

                <!-- 24小时活动热力图 -->
                <div v-if="activityHeatmapChartOption" class="border rounded-lg bg-white p-4 shadow-sm lg:col-span-2 dark:bg-gray-900">
                  <VChart :option="activityHeatmapChartOption" class="h-96" autoresize />
                </div>

                <!-- 应用使用时段分布雷达图 -->
                <div v-if="timeDistributionRadarChartOption" class="border rounded-lg bg-white p-4 shadow-sm lg:col-span-2 dark:bg-gray-900">
                  <VChart :option="timeDistributionRadarChartOption" class="h-96" autoresize />
                </div>
              </div>

              <!-- 统计摘要 -->
              <div class="mt-4 border-t border-gray-300 pt-4 dark:border-gray-700">
                <h4 class="mb-3 text-gray-700 font-semibold dark:text-gray-300">
                  统计摘要
                </h4>
                <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
                  <div class="border rounded-lg bg-white p-3 text-center dark:bg-gray-900">
                    <div class="text-2xl text-primary font-bold">
                      {{ testResults.useAppRecords.length }}
                    </div>
                    <div class="text-xs text-gray-500">
                      总记录数
                    </div>
                  </div>
                  <div class="border rounded-lg bg-white p-3 text-center dark:bg-gray-900">
                    <div class="text-success text-2xl font-bold">
                      {{ new Set(testResults.useAppRecords.map((r: any) => r.appId)).size }}
                    </div>
                    <div class="text-xs text-gray-500">
                      使用过的应用数
                    </div>
                  </div>
                  <div class="border rounded-lg bg-white p-3 text-center dark:bg-gray-900">
                    <div class="text-warning text-2xl font-bold">
                      {{ (testResults.useAppRecords.reduce((sum: number, r: any) => sum + r.duration, 0) / 3600).toFixed(2) }}h
                    </div>
                    <div class="text-xs text-gray-500">
                      总使用时长
                    </div>
                  </div>
                  <div class="border rounded-lg bg-white p-3 text-center dark:bg-gray-900">
                    <div class="text-danger text-2xl font-bold">
                      {{ (testResults.useAppRecords.reduce((sum: number, r: any) => sum + r.duration, 0) / testResults.useAppRecords.length / 60).toFixed(2) }}min
                    </div>
                    <div class="text-xs text-gray-500">
                      平均每次使用时长
                    </div>
                  </div>
                </div>
              </div>
            </div>
            <div v-else>
              <el-empty description="暂无有效使用记录" />
            </div>
          </div>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped lang="scss">
pre {
  padding: 12px;
  overflow-x: auto;
  background: var(--el-bg-color);
  border-radius: 4px;
}
</style>
