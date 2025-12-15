import type { LoginAccountParams, SubmitCodeParams } from './steam/test/loginSteamWithAccount'
import type { AppSettings } from './types/settings'
import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { app, BrowserWindow, globalShortcut, ipcMain, Menu, protocol, screen, shell, Tray } from 'electron'
import { closeDatabase } from './db/connection'
import { getJobStatus, setUpdateInterval, startUpdateAppRunningStatusJob, stopUpdateAppRunningStatusJob } from './job/updateAppRunningStatusJob'
import * as globalStatusService from './service/globalStatusService'
import * as settingsService from './service/settingsService'
import * as steamAppService from './service/steamAppService'
import * as steamUserService from './service/steamUserService'
import * as updateService from './service/updateService'
import * as useAppRecordService from './service/useAppRecordService'
import { cancelLoginSession, startLoginWithAccount, submitSteamGuardCode } from './steam/test/loginSteamWithAccount'
import { cancelQRLoginSession, startLoginWithQRCode } from './steam/test/loginSteamWithQRCode'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

// 区分开发环境和生产环境的 userData 路径
if (!app.isPackaged) {
  app.setName('steam-stat-dev')
  app.setPath('userData', path.join(app.getPath('appData'), 'steam-stat-dev'))
}

process.env.DIST = path.join(__dirname, '../dist')
process.env.VITE_PUBLIC = app.isPackaged ? process.env.DIST : path.join(process.env.DIST, '../public')

let win: BrowserWindow
let tray: Tray | null = null
let isQuitting = false
let closeConfirmAction: 'minimize' | 'exit' | 'ask' | 'ignore' = 'ignore'

const VITE_DEV_SERVER_URL = process.env.VITE_DEV_SERVER_URL

// 设置逻辑像素
const LOGICAL_WIDTH = 1920
const LOGICAL_HEIGHT = 1080
const MIN_LOGICAL_WIDTH = 1600
const MIN_LOGICAL_HEIGHT = 900

function createWindow() {
  // 窗口图标：256x256
  const windowIconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'icons8-steam-256.ico')
    : path.join(__dirname, '../resources/icons8-steam-256.ico')

  // 托盘图标：16x16 或 32x32 最佳
  const trayIconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'icons8-steam-256.ico')
    : path.join(__dirname, '../resources/icons8-steam-256.ico')

  // 检查小图标是否存在，不存在则降级使用大图标
  const finalTrayIcon = fs.existsSync(trayIconPath) ? trayIconPath : windowIconPath

  const primary = screen.getPrimaryDisplay()
  const scaleFactor = primary.scaleFactor // Windows 缩放 100%→1.0, 150%→1.5, 200%→2.0

  const width = Math.round(LOGICAL_WIDTH / scaleFactor)
  const height = Math.round(LOGICAL_HEIGHT / scaleFactor)
  const minWidth = Math.round(MIN_LOGICAL_WIDTH / scaleFactor)
  const minHeight = Math.round(MIN_LOGICAL_HEIGHT / scaleFactor)

  win = new BrowserWindow({
    width,
    height,
    minWidth,
    minHeight,
    roundedCorners: true,
    icon: windowIconPath,
    show: true,
    skipTaskbar: false,
    alwaysOnTop: false,
    autoHideMenuBar: true,
    titleBarStyle: 'default',
    webPreferences: {
      preload: path.join(__dirname, 'preload.mjs'),
      contextIsolation: true,
      nodeIntegration: false,
      zoomFactor: 1.0 / scaleFactor,
    },
  })

  // 设置 CSP 以解决安全警告
  win.webContents.session.webRequest.onHeadersReceived((details, callback) => {
    callback({
      responseHeaders: {
        ...details.responseHeaders,
        'Content-Security-Policy': [
          'default-src \'self\'; '
          + 'script-src \'self\'; '
          + 'style-src \'self\' \'unsafe-inline\'; '
          + 'img-src \'self\' data: https: steam-avatar:; '
          + 'font-src \'self\' data:; '
          + 'connect-src \'self\' https:;',
        ],
      },
    })
  })

  // 在窗口内容加载完成后再打开 DevTools
  win.webContents.on('did-finish-load', () => {
    const sf = screen.getPrimaryDisplay().scaleFactor
    win.webContents.setZoomFactor(1 / sf)
    if (!app.isPackaged) {
      win.webContents.openDevTools({ mode: 'detach' })
    }
  })

  // 生产环境：禁用 Ctrl+R 和 F5 等刷新快捷键
  // Electron 默认启用这些快捷键，在生产环境需要禁用以防止用户误操作
  if (app.isPackaged) {
    win.webContents.on('before-input-event', (event, input) => {
      // 禁用 Ctrl+R 刷新
      if (input.control && input.key.toLowerCase() === 'r') {
        event.preventDefault()
      }
      // 禁用 F5 刷新
      if (input.key === 'F5') {
        event.preventDefault()
      }
      // 禁用 Ctrl+Shift+R 强制刷新
      if (input.control && input.shift && input.key.toLowerCase() === 'r') {
        event.preventDefault()
      }
      // 禁用 Ctrl+F5 强制刷新
      if (input.control && input.key === 'F5') {
        event.preventDefault()
      }
      // 禁用 Alt 弹出菜单
      if (input.alt) {
        event.preventDefault()
      }
    })
  }

  // 系统托盘
  tray = new Tray(finalTrayIcon)
  const contextMenuTemplate = [
    {
      label: '退出 (Exit) Steam Stat',
      click: () => {
        closeConfirmAction = 'exit'
        win.close()
      },
    },
  ]
  const contextMenu = Menu.buildFromTemplate(contextMenuTemplate)
  tray.setToolTip('Steam Stat')
  tray.setContextMenu(contextMenu)
  tray.on('click', () => {
    win.setSkipTaskbar(false)
    win.show()
  })

  // 窗口关闭事件：根据设置决定是退出还是最小化到托盘
  win.on('close', async (event) => {
    // 如果是真正的退出，不阻止
    if (isQuitting) {
      return
    }

    // 需要弹出确认框，由渲染进程处理
    event.preventDefault()

    // 先显示窗口
    win.setSkipTaskbar(false)
    win.show()

    // 发送事件到渲染进程，让其显示确认对话框
    win.webContents.send('show-close-confirm', {
      action: closeConfirmAction,
    })

    closeConfirmAction = 'ignore'
  })

  if (VITE_DEV_SERVER_URL) {
    win.loadURL(VITE_DEV_SERVER_URL)
  }
  else {
    win.loadFile(path.join(process.env.DIST || '', 'index.html'))
  }
}

// 单例模式：确保只运行一个应用实例
const gotTheLock = app.requestSingleInstanceLock()

if (!gotTheLock) {
  // 如果获取锁失败，说明已经有实例在运行，退出当前实例
  app.quit()
}
else {
  // 当第二个实例尝试启动时，将焦点放在已有实例的窗口上
  app.on('second-instance', () => {
    if (win) {
      // 如果窗口被隐藏或最小化，则显示它
      win.show()
      if (win.isMinimized()) {
        win.restore()
      }
      win.setSkipTaskbar(false)
      win.focus()
    }
  })
}

// 窗口关闭
app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit()
  }
})

app.whenReady().then(async () => {
  // 注册自定义协议用于读取本地文件（解决渲染进程无法读取本地文件的问题）
  protocol.registerFileProtocol('steam-avatar', (request, callback) => {
    const url = request.url.replace('steam-avatar://', '')
    try {
      // 解码 URL 编码的路径
      const decodedPath = decodeURIComponent(url)
      return callback({ path: decodedPath })
    }
    catch (error) {
      console.error('[Protocol] 注册 steam-avatar 协议失败:', error)
      return callback({ error: -2 }) // net::ERR_FAILED
    }
  })

  // 加载应用设置
  const settings = settingsService.getSettings()

  console.warn('[Settings] 已加载语言:', app.getLocale())

  // 设置开机自启
  app.setLoginItemSettings({
    openAtLogin: settings.autoStart,
    path: app.getPath('exe'),
  })

  // 初始化数据库数据
  await globalStatusService.initOrUpdateGlobalStatus()
  const globalStatus = await globalStatusService.getGlobalStatus()
  await steamUserService.initOrUpdateSteamUser(globalStatus.steamPath!)
  await steamAppService.initOrUpdateSteamApp(globalStatus.steamPath!)

  // 初始化应用使用记录（结束所有未完成的记录）
  await useAppRecordService.initOrUpdateUseAppRecord()

  // 根据设置启动定时任务：更新应用运行状态
  if (settings.updateAppRunningStatusJob.enabled) {
    setUpdateInterval(settings.updateAppRunningStatusJob.intervalSeconds * 1000)
    startUpdateAppRunningStatusJob()
  }

  // 初始化窗口
  createWindow()

  // 初始化自动更新
  updateService.setupAutoUpdater(win, settings.autoUpdate)

  // 监听显示器缩放变化
  screen.on('display-metrics-changed', (event, display, changedMetrics) => {
    if (changedMetrics.includes('scaleFactor')) {
      const scaleFactor = display.scaleFactor
      const win = BrowserWindow.getAllWindows()[0]

      win.setSize(
        Math.round(LOGICAL_WIDTH / scaleFactor),
        Math.round(LOGICAL_HEIGHT / scaleFactor),
      )
      win.setMinimumSize(
        Math.round(MIN_LOGICAL_WIDTH / scaleFactor),
        Math.round(MIN_LOGICAL_HEIGHT / scaleFactor),
      )

      // win.webContents.setZoomFactor(1.0 / (scaleFactor > 1.5 ? scaleFactor / 1.25 : scaleFactor))
      win.webContents.setZoomFactor(1.0 / scaleFactor)
    }
  })

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow()
    }
  })

  // 注册 Steam Test API
  ipcMain.handle('steam-getLoginUser', async () => {
    return await steamUserService.getSteamLoginUserInfo()
  })
  ipcMain.handle('steam-refreshLoginUser', async () => {
    const globalStatus = await globalStatusService.getGlobalStatus()
    return await steamUserService.refreshSteamLoginUserInfo(globalStatus.steamPath!)
  })
  ipcMain.handle('steam-getStatus', async () => {
    return await globalStatusService.getGlobalStatus()
  })
  ipcMain.handle('steam-refreshStatus', async () => {
    return await globalStatusService.refreshGlobalStatus()
  })
  ipcMain.handle('steam-getRunningApps', async () => {
    return await steamAppService.getRunningSteamAppInfo()
  })
  ipcMain.handle('steam-getAppsInfo', async () => {
    return await steamAppService.getSteamAppInfo()
  })
  ipcMain.handle('steam-refreshAppsInfo', async () => {
    const globalStatus = await globalStatusService.getGlobalStatus()
    return await steamAppService.refreshSteamAppInfo(globalStatus.steamPath!)
  })
  ipcMain.handle('steam-getLibraryFolders', async () => {
    const globalStatus = await globalStatusService.getGlobalStatus()
    return await steamAppService.getLibraryFolders(globalStatus.steamPath!)
  })
  ipcMain.handle('steam-getValidUseAppRecord', async (_event, steamIds?: bigint[], startDate?: number, endDate?: number) => {
    return await useAppRecordService.getValidUseAppRecord(steamIds, startDate, endDate)
  })

  // 账号密码登录
  ipcMain.handle('steam:login:account:start', async (_event, params: LoginAccountParams) => {
    return await startLoginWithAccount(params, win)
  })

  // 提交验证码
  ipcMain.handle('steam:login:account:submitCode', async (_event, params: SubmitCodeParams) => {
    return await submitSteamGuardCode(params)
  })

  // 取消账号密码登录
  ipcMain.handle('steam:login:account:cancel', async (_event, sessionId: string) => {
    cancelLoginSession(sessionId)
    return { success: true }
  })

  // 二维码登录
  ipcMain.handle('steam:login:qr:start', async (_event, httpProxy?: string) => {
    return await startLoginWithQRCode(win, httpProxy)
  })

  // 取消二维码登录
  ipcMain.handle('steam:login:qr:cancel', async (_event, sessionId: string) => {
    cancelQRLoginSession(sessionId)
    return { success: true }
  })

  // 定时任务相关 API
  ipcMain.handle('job:updateAppRunningStatus:getStatus', async () => {
    return getJobStatus()
  })

  ipcMain.handle('job:updateAppRunningStatus:start', async () => {
    startUpdateAppRunningStatusJob()
    return { success: true }
  })

  ipcMain.handle('job:updateAppRunningStatus:stop', async () => {
    stopUpdateAppRunningStatusJob()
    return { success: true }
  })

  ipcMain.handle('job:updateAppRunningStatus:setInterval', async (_event, intervalSeconds: number) => {
    // 将秒转换为毫秒
    const intervalMs = intervalSeconds * 1000
    setUpdateInterval(intervalMs)
    return { success: true }
  })

  // 在默认浏览器中打开外部链接
  ipcMain.handle('shell:openExternal', async (_event, url: string) => {
    try {
      await shell.openExternal(url)
      return { success: true }
    }
    catch (error) {
      return { success: false, error: String(error) }
    }
  })

  // 打开文件夹路径
  ipcMain.handle('shell:openPath', async (_event, path: string) => {
    try {
      const result = await shell.openPath(path)
      return { success: !result, error: result }
    }
    catch (error) {
      return { success: false, error: String(error) }
    }
  })

  // 设置相关 API
  ipcMain.handle('settings:get', async () => {
    return settingsService.getSettings()
  })

  ipcMain.handle('settings:save', async (_event, settings: Partial<AppSettings>) => {
    const success = settingsService.saveSettings(settings)
    if (success && settings.autoStart !== undefined) {
      // 更新开机自启设置
      app.setLoginItemSettings({
        openAtLogin: settings.autoStart,
        path: app.getPath('exe'),
      })
    }
    return { success }
  })

  ipcMain.handle('settings:update', async (_event, partialSettings: Partial<AppSettings>) => {
    const success = settingsService.updateSettings(partialSettings)
    if (success && partialSettings.autoStart !== undefined) {
      // 更新开机自启设置
      app.setLoginItemSettings({
        openAtLogin: partialSettings.autoStart,
        path: app.getPath('exe'),
      })
    }
    return { success }
  })

  ipcMain.handle('settings:reset', async () => {
    const success = settingsService.resetSettings()
    if (success) {
      // 重置开机自启设置
      app.setLoginItemSettings({
        openAtLogin: false,
        path: app.getPath('exe'),
      })
    }
    return { success }
  })

  ipcMain.handle('settings:getAutoStart', async () => {
    const loginSettings = app.getLoginItemSettings()
    return loginSettings.openAtLogin
  })

  ipcMain.handle('settings:getCloseAction', async () => {
    const settings = settingsService.getSettings()
    return settings.closeAction
  })

  ipcMain.handle('settings:setCloseAction', async (_event, action: 'exit' | 'minimize' | 'ask') => {
    const success = settingsService.updateSettings({ closeAction: action })
    return { success }
  })

  // 应用窗口相关 API
  ipcMain.handle('app:minimizeToTray', async () => {
    win.hide()
    win.setSkipTaskbar(true)
    return { success: true }
  })

  ipcMain.handle('app:quit', async () => {
    isQuitting = true
    app.quit()
    return { success: true }
  })

  // 处理退出时的正在运行记录
  ipcMain.handle('useAppRecord:endCurrentRunning', async () => {
    try {
      await useAppRecordService.endAllRunningRecords()
      return { success: true }
    }
    catch (error: any) {
      return { success: false, error: error.message }
    }
  })

  ipcMain.handle('useAppRecord:discardCurrentRunning', async () => {
    try {
      await useAppRecordService.discardAllRunningRecords()
      return { success: true }
    }
    catch (error: any) {
      return { success: false, error: error.message }
    }
  })

  // 更新相关 API
  ipcMain.handle('update:checkForUpdates', async () => {
    try {
      await updateService.checkForUpdates()
      return { success: true }
    }
    catch (error: any) {
      return { success: false, error: error.message }
    }
  })

  ipcMain.handle('update:downloadUpdate', async () => {
    updateService.downloadUpdate()
    return { success: true }
  })

  ipcMain.handle('update:quitAndInstall', async () => {
    updateService.quitAndInstall()
    return { success: true }
  })

  ipcMain.handle('update:getCurrentVersion', async () => {
    return updateService.getCurrentVersion()
  })

  ipcMain.handle('update:getStatus', async () => {
    return updateService.getUpdateStatus()
  })

  ipcMain.handle('update:setAutoUpdate', async (_event, enabled: boolean) => {
    updateService.setAutoUpdate(enabled)
    // 同时更新设置
    const success = settingsService.updateSettings({ autoUpdate: enabled })
    return { success }
  })
})

// 应用退出前清理资源
app.on('before-quit', async () => {
  // 停止定时任务
  stopUpdateAppRunningStatusJob()

  // 关闭数据库连接
  closeDatabase()

  // 注销全局快捷键
  globalShortcut.unregisterAll()
})
