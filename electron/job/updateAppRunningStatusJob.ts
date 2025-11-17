/**
 * 定时更新应用运行状态任务
 */
import * as steamAppService from '../service/steamAppService'

let intervalId: NodeJS.Timeout | null = null
let intervalTime = 5000 // 默认5秒
let isRunning = false
let lastUpdateTime = 0

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
    await steamAppService.updateAppRunningStatus()
    lastUpdateTime = Date.now()
    console.warn('[Job] 应用运行状态更新成功')
  }
  catch (error) {
    console.error('[Job] 应用运行状态更新失败:', error)
  }
}
