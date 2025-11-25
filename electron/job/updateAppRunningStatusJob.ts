/**
 * 定时更新应用运行状态任务
 */
import * as globalStatusService from '../service/globalStatusService'
import * as localRegService from '../service/localRegService'
import * as steamAppService from '../service/steamAppService'
import * as useAppRecordService from '../service/useAppRecordService'

let intervalId: NodeJS.Timeout | null = null
let intervalTime = 5000 // 默认5秒
let isRunning = false
let lastUpdateTime = 0
let lastRunningApps: number[] = [] // 上一次运行的应用列表

/**
 * 启动定时更新任务
 */
export function startUpdateAppRunningStatusJob() {
  if (isRunning) {
    console.warn('[Job] 应用运行状态更新任务已在运行')
    return
  }

  console.warn(`[Job] 启动应用运行状态更新任务，更新间隔: ${intervalTime}ms`)
  isRunning = true

  // 立即执行一次
  updateAppRunningStatus()

  // 启动定时任务
  intervalId = setInterval(() => {
    updateAppRunningStatus()
  }, intervalTime)
}

/**
 * 停止定时更新任务
 */
export function stopUpdateAppRunningStatusJob() {
  if (!isRunning) {
    console.warn('[Job] 应用运行状态更新任务未在运行')
    return
  }

  console.warn('[Job] 停止应用运行状态更新任务')
  if (intervalId) {
    clearInterval(intervalId)
    intervalId = null
  }
  isRunning = false
}

/**
 * 设置更新间隔时间（毫秒）
 */
export function setUpdateInterval(interval: number) {
  if (interval < 1000) {
    console.warn('[Job] 更新间隔时间不能小于1000ms，已自动设置为1000ms')
    interval = 1000
  }

  intervalTime = interval
  console.warn(`[Job] 应用运行状态更新间隔已设置为: ${intervalTime}ms`)

  // 如果任务正在运行，重启以应用新的间隔时间
  if (isRunning) {
    stopUpdateAppRunningStatusJob()
    startUpdateAppRunningStatusJob()
  }
}

/**
 * 获取当前任务状态
 */
export function getJobStatus() {
  return {
    isRunning,
    intervalTime,
    lastUpdateTime,
  }
}

/**
 * 执行更新操作
 */
async function updateAppRunningStatus() {
  try {
    // 读取当前运行的应用列表
    const currentRunningApps = await localRegService.readRunningAppsReg()

    // 比较新旧值，检测变化
    const added = currentRunningApps.filter(appId => !lastRunningApps.includes(appId))
    const removed = lastRunningApps.filter(appId => !currentRunningApps.includes(appId))

    // 只在有变化时才更新数据库
    if (added.length > 0 || removed.length > 0) {
      console.warn(`[Job] 检测到运行应用变化: 新增 ${added.length} 个, 移除 ${removed.length} 个`)

      // 获取当前活跃用户的 SteamID
      const globalStatus = await globalStatusService.refreshGlobalStatus()
      const activeSteamId = globalStatus?.activeUserSteamId

      await steamAppService.refreshSteamAppInfo(globalStatus.steamPath!)

      // 更新应用运行状态
      await steamAppService.updateAppRunningStatus(currentRunningApps)

      if (activeSteamId) {
        // 记录新增的应用
        for (const appId of added) {
          await useAppRecordService.startRecord(activeSteamId, appId)
        }

        // 结束移除的应用
        for (const appId of removed) {
          await useAppRecordService.stopRecord(activeSteamId, appId)
        }
      }
      else {
        console.warn('[Job] 未找到活跃用户 SteamID，跳过记录应用使用')
      }

      // 更新上一次的运行应用列表
      lastRunningApps = currentRunningApps
    }

    // 更新上一次的检测更新时间
    lastUpdateTime = Date.now()
  }
  catch (error) {
    console.error('[Job] 应用运行状态更新失败:', error)
  }
}
