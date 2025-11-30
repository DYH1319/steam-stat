import type { BrowserWindow } from 'electron'
import type { ProgressInfo, UpdateInfo } from 'electron-updater'
import { app } from 'electron'
import { autoUpdater } from 'electron-updater'

let mainWindow: BrowserWindow | null = null
let autoUpdateEnabled = true
let checkUpdateInterval: NodeJS.Timeout | null = null
let isChecking = false
let isDownloading = false

/**
 * 配置自动更新
 */
export function setupAutoUpdater(window: BrowserWindow, autoUpdate: boolean) {
  mainWindow = window
  autoUpdateEnabled = autoUpdate

  // 配置更新源（GitHub Releases）
  autoUpdater.setFeedURL({
    provider: 'github',
    owner: 'DYH1319',
    repo: 'steam-stat',
  })

  // 关闭自动下载，手动控制下载时机
  autoUpdater.autoDownload = false

  // 关闭自动安装，给用户选择权
  autoUpdater.autoInstallOnAppQuit = true

  // 检查更新时的事件
  autoUpdater.on('checking-for-update', () => {
    isChecking = true
    console.warn('[Updater] 正在检查更新...')
    sendUpdateEvent('checking-for-update')
  })

  // 有可用更新
  autoUpdater.on('update-available', (info: UpdateInfo) => {
    isChecking = false
    console.warn('[Updater] 发现新版本:', info.version)
    sendUpdateEvent('update-available', {
      version: info.version,
      releaseDate: info.releaseDate,
      files: info.files,
    })

    // 自动更新模式下自动下载
    if (autoUpdateEnabled) {
      autoUpdater.downloadUpdate()
    }
  })

  // 没有可用更新
  autoUpdater.on('update-not-available', (info: UpdateInfo) => {
    isChecking = false
    console.warn('[Updater] 当前已是最新版本:', info.version)
    sendUpdateEvent('update-not-available', { version: info.version })
  })

  // 下载进度
  autoUpdater.on('download-progress', (progressObj: ProgressInfo) => {
    isDownloading = true
    sendUpdateEvent('download-progress', {
      percent: progressObj.percent,
      bytesPerSecond: progressObj.bytesPerSecond,
      transferred: progressObj.transferred,
      total: progressObj.total,
    })
  })

  // 下载完成
  autoUpdater.on('update-downloaded', (info: UpdateInfo) => {
    isDownloading = false
    console.warn('[Updater] 更新下载完成:', info.version)
    sendUpdateEvent('update-downloaded', { version: info.version })
  })

  // 更新错误
  autoUpdater.on('error', (error: Error) => {
    isChecking = false
    isDownloading = false
    console.error('[Updater] 更新错误:', error)
    sendUpdateEvent('update-error', { message: error.message })
  })

  // 启动时检查更新（延迟5秒，避免阻塞启动）
  if (autoUpdateEnabled) {
    setTimeout(() => {
      checkForUpdates()
    }, 5000)
  }

  // 设置定期检查
  setupAutoCheckInterval(autoUpdateEnabled)
}

/**
 * 发送更新事件到渲染进程
 */
function sendUpdateEvent(event: string, data?: any) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send('update-event', { event, data })
  }
}

/**
 * 设置自动检查间隔
 */
function setupAutoCheckInterval(enabled: boolean) {
  // 清除现有的定时器
  if (checkUpdateInterval) {
    clearInterval(checkUpdateInterval)
    checkUpdateInterval = null
  }

  // 如果启用自动更新，每4小时检查一次
  if (enabled) {
    checkUpdateInterval = setInterval(() => {
      checkForUpdates()
    }, 4 * 60 * 60 * 1000)
  }
}

/**
 * 手动检查更新
 */
export function checkForUpdates() {
  if (isChecking) {
    console.warn('[Updater] 正在检查更新中，请稍后再试')
    return Promise.resolve()
  }

  return autoUpdater.checkForUpdates().catch((error: Error) => {
    isChecking = false
    console.error('[Updater] 检查更新失败:', error)
    throw error
  })
}

/**
 * 下载更新
 */
export function downloadUpdate() {
  if (isDownloading) {
    console.warn('[Updater] 正在下载更新中')
    return
  }
  autoUpdater.downloadUpdate()
}

/**
 * 退出并安装更新
 */
export function quitAndInstall() {
  autoUpdater.quitAndInstall(false, true)
}

/**
 * 获取当前版本
 */
export function getCurrentVersion() {
  return app.getVersion()
}

/**
 * 设置自动更新开关
 */
export function setAutoUpdate(enabled: boolean) {
  autoUpdateEnabled = enabled
  setupAutoCheckInterval(enabled)

  // 如果启用了自动更新，立即检查一次
  if (enabled) {
    setTimeout(() => {
      checkForUpdates()
    }, 1000)
  }
}

/**
 * 获取更新状态
 */
export function getUpdateStatus() {
  return {
    isChecking,
    isDownloading,
    autoUpdateEnabled,
    currentVersion: getCurrentVersion(),
  }
}
