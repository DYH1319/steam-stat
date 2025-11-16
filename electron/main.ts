import type { LoginAccountParams, SubmitCodeParams } from './steam/test/loginSteamWithAccount'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { app, BrowserWindow, globalShortcut, ipcMain, Menu, Tray } from 'electron'
import { closeDatabase, initDatabase } from './db/connection'
import * as steamUserService from './service/steamUserService'
import { getInstalledApps } from './steam/test/getInstalledApps'
import { getLibraryFolders } from './steam/test/getLibraryFolders'
import { getRunningAppsByRegQuery } from './steam/test/getRunningApps'
import { getSteamStatus as getSteamStatusTest } from './steam/test/getSteamStatus'
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
  // 初始化数据库连接
  initDatabase()

  // 初始化用户数据
  await steamUserService.initOrUpdateSteamUserDb()

  // 初始化窗口
  createWindow()

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow()
    }
  })

  // 测试 appinfo.vdf 解析（可选）
  // 取消注释以下代码来测试 appinfo.vdf 解析
  // try {
  //   // const vdf = await import('./util/binaryVdf')
  //   // const data = vdf.readBinaryVdf('D:\\Program Files (x86)\\Steam\\config\\coplay_76561198928433910.vdf')
  //   // console.warn(data)

  //   const { testParseAppInfo } = await import('./steam/appinfo.test')

  //   // 测试 TypeScript 解析器
  //   await testParseAppInfo()

  //   // 运行 C# 解析器并对比结果（已编译的 exe，无需安装 .NET SDK）
  //   // 取消下面的注释来运行对比测试：
  //   const { runCSharpParserAndCompare } = await import('./steam/appinfo.test')
  //   await runCSharpParserAndCompare()
  // }
  // catch (error) {
  //   console.error('[Main] appinfo.vdf 测试失败:', error)
  // }

  // 注册 Steam Test API
  ipcMain.handle('steam-test-getLoginUser', async () => {
    return await steamUserService.getSteamLoginUserInfo()
  })
  ipcMain.handle('steam-test-getStatus', async () => {
    return await getSteamStatusTest()
  })
  ipcMain.handle('steam-test-getRunningApps', async () => {
    return await getRunningAppsByRegQuery()
  })
  ipcMain.handle('steam-test-getInstalledApps', async () => {
    return await getInstalledApps()
  })
  ipcMain.handle('steam-test-getLibraryFolders', async () => {
    return await getLibraryFolders()
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

// 应用退出前关闭数据库
app.on('before-quit', () => {
  // stopMonitor()
  closeDatabase() // 使用新的数据库关闭函数
  globalShortcut.unregisterAll()
})
