import type { LoginAccountParams, SubmitCodeParams } from './steam/test/loginSteamWithAccount'
import type { AppSettings } from './types/settings'
import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { app, BrowserWindow, globalShortcut, ipcMain, Menu, shell, Tray } from 'electron'
import { closeDatabase } from './db/connection'
import { getJobStatus, setUpdateInterval, startUpdateAppRunningStatusJob, stopUpdateAppRunningStatusJob } from './job/updateAppRunningStatusJob'
import * as globalStatusService from './service/globalStatusService'
import * as settingsService from './service/settingsService'
import * as steamAppService from './service/steamAppService'
import * as steamUserService from './service/steamUserService'
import * as useAppRecordService from './service/useAppRecordService'
import { cancelLoginSession, startLoginWithAccount, submitSteamGuardCode } from './steam/test/loginSteamWithAccount'
import { cancelQRLoginSession, startLoginWithQRCode } from './steam/test/loginSteamWithQRCode'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

process.env.DIST = path.join(__dirname, '../dist')
process.env.VITE_PUBLIC = app.isPackaged ? process.env.DIST : path.join(process.env.DIST, '../public')

let win: BrowserWindow

const VITE_DEV_SERVER_URL = process.env.VITE_DEV_SERVER_URL

function createWindow() {
  // 窗口图标：256x256 最佳，512x512 也可接受
  const windowIconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'steam-square-256.png')
    : path.join(__dirname, '../resources/steam-square-256.png')

  // 托盘图标：16x16 或 32x32 最佳
  // 如果没有专用小图标，先使用大图标（系统会自动缩放，但效果不佳）
  const trayIconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'steam-square-32.png')
    : path.join(__dirname, '../resources/steam-square-32.png')

  // 检查小图标是否存在，不存在则降级使用大图标
  const finalTrayIcon = fs.existsSync(trayIconPath) ? trayIconPath : windowIconPath

  win = new BrowserWindow({
    width: 1920,
    height: 1080,
    minWidth: 1600,
    minHeight: 900,
    roundedCorners: true,
    icon: windowIconPath,
    show: true,
    skipTaskbar: false,
    alwaysOnTop: false,
    autoHideMenuBar: true,
    titleBarStyle: 'default',
    webPreferences: {
      preload: path.join(__dirname, 'preload.mjs'),
      contextIsolation: true, // ✅ 如果有这行，建议暂时关掉试试
      nodeIntegration: false, // ✅ 也可能导致 F12 无效
      devTools: true, // ✅ 显式启用开发者工具
    },
  })

  // ✅ 在窗口内容加载完成后再打开 DevTools
  win.webContents.on('did-finish-load', () => {
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
    })
  }

  // 系统托盘 - 使用专门的小尺寸图标
  const tray = new Tray(finalTrayIcon)
  const contextMenuTemplate = [
    {
      label: '退出 Steam Stat',
      click: () => {
        app.quit()
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

  if (VITE_DEV_SERVER_URL) {
    win.loadURL(VITE_DEV_SERVER_URL)
  }
  else {
    win.loadFile(path.join(process.env.DIST || '', 'index.html'))
  }
}

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit()
  }
})

app.whenReady().then(async () => {
  // 加载应用设置
  const settings = settingsService.getSettings()

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
  ipcMain.handle('steam-getValidUseAppRecord', async () => {
    return await useAppRecordService.getValidUseAppRecord()
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
