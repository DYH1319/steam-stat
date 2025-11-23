import type { LoginAccountParams, SubmitCodeParams } from './steam/test/loginSteamWithAccount'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { app, BrowserWindow, globalShortcut, ipcMain, Menu, Tray } from 'electron'
import { closeDatabase } from './db/connection'
import { startUpdateAppRunningStatusJob, stopUpdateAppRunningStatusJob } from './job/updateAppRunningStatusJob'
import * as globalStatusService from './service/globalStatusService'
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
  win = new BrowserWindow({
    width: 1920,
    height: 1080,
    minWidth: 1600,
    minHeight: 900,
    roundedCorners: true,
    icon: path.join(process.resourcesPath, 'icons8-steam-512.png'),
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

  // 系统托盘
  const iconPath = app.isPackaged
    ? path.join(process.resourcesPath, 'icons8-steam-512.png')
    : path.join(__dirname, '../resources/icons8-steam-512.png')
  const tray = new Tray(iconPath)
  const contextMenuTemplate = [
    {
      label: '退出 Steam Stat',
      click: () => {
        app.quit()
      },
    },
    {
      label: 'Open a Dialog',
      click: () => win.webContents.openDevTools({ mode: 'detach' }),
      accelerator: 'F12',
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
  // 初始化数据库数据
  await globalStatusService.initOrUpdateGlobalStatus()
  const globalStatus = await globalStatusService.getGlobalStatus()
  await steamUserService.initOrUpdateSteamUser(globalStatus.steamPath!)
  await steamAppService.initOrUpdateSteamApp(globalStatus.steamPath!)

  // 初始化应用使用记录（结束所有未完成的记录）
  await useAppRecordService.initOrUpdateUseAppRecord()

  // 启动定时任务：更新应用运行状态
  startUpdateAppRunningStatusJob()

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
