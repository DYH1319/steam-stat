/**
 * 游戏监控模块
 * 定时检测正在运行的 Steam 游戏并记录到数据库
 */

import { endGameRecord, getActiveRecordByAppId, getActiveRecords, startGameRecord } from '../db/records'
import { getCurrentUser } from '../db/users'
import { getSteamGamesStatus } from '../steam'

let monitorInterval: NodeJS.Timeout | null = null
const currentRunningGames: Map<string, number> = new Map() // appId -> recordId

/**
 * 开始监控
 */
export function startMonitor(intervalSeconds: number = 60): void {
  if (monitorInterval) {
    console.warn('[Monitor] 监控已在运行中')
    return
  }

  console.warn(`[Monitor] 启动监控，间隔: ${intervalSeconds}秒`)

  // 立即执行一次
  checkRunningGames()

  // 设置定时任务
  monitorInterval = setInterval(() => {
    checkRunningGames()
  }, intervalSeconds * 1000)
}

/**
 * 停止监控
 */
export function stopMonitor(): void {
  if (monitorInterval) {
    clearInterval(monitorInterval)
    monitorInterval = null

    // 结束所有进行中的记录
    for (const recordId of currentRunningGames.values()) {
      endGameRecord(recordId)
    }
    currentRunningGames.clear()

    console.warn('[Monitor] 监控已停止')
  }
}

/**
 * 更新监控间隔
 */
export function updateMonitorInterval(intervalSeconds: number): void {
  if (monitorInterval) {
    stopMonitor()
    startMonitor(intervalSeconds)
  }
}

/**
 * 获取监控状态
 */
export function getMonitorStatus(): { isRunning: boolean, runningGames: number } {
  return {
    isRunning: monitorInterval !== null,
    runningGames: currentRunningGames.size,
  }
}

/**
 * 检查正在运行的游戏
 */
async function checkRunningGames(): Promise<void> {
  try {
    const status = await getSteamGamesStatus()
    const runningGames = status.runningGames || []
    const currentUser = getCurrentUser()

    // 获取当前正在运行的游戏 AppID 集合
    const runningAppIds = new Set(runningGames.map(game => game.appId))

    // 检查已经结束的游戏
    for (const [appId, recordId] of currentRunningGames.entries()) {
      if (!runningAppIds.has(appId)) {
        // 游戏已经停止运行
        endGameRecord(recordId)
        currentRunningGames.delete(appId)
        console.warn(`[Monitor] 游戏停止运行: ${appId}`)
      }
    }

    // 检查新开始的游戏
    for (const game of runningGames) {
      if (!currentRunningGames.has(game.appId)) {
        // 检查数据库中是否已有活跃记录
        const activeRecord = getActiveRecordByAppId(game.appId)

        if (activeRecord) {
          // 如果已有活跃记录，使用它
          currentRunningGames.set(game.appId, activeRecord.id!)
          console.warn(`[Monitor] 发现已存在的活跃记录: ${game.name}`)
        }
        else {
          // 新游戏开始运行
          const recordId = startGameRecord({
            appId: game.appId,
            gameName: game.name,
            steamId: currentUser?.steamId,
            exePath: (game as any).matchedExePath,
            pid: game.process?.pid,
          })
          currentRunningGames.set(game.appId, recordId)
          console.warn(`[Monitor] 游戏开始运行: ${game.name}`)
        }
      }
    }

    console.warn(`[Monitor] 检查完成，当前运行游戏数: ${currentRunningGames.size}`)
  }
  catch (error) {
    console.error('[Monitor] 检查运行游戏失败:', error)
  }
}

/**
 * 初始化监控（从数据库恢复活跃记录）
 */
export function initMonitor(): void {
  const activeRecords = getActiveRecords()
  for (const record of activeRecords) {
    currentRunningGames.set(record.appId, record.id!)
  }
  console.warn(`[Monitor] 初始化完成，恢复 ${activeRecords.length} 个活跃记录`)
}
