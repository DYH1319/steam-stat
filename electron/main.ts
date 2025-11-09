import type { SteamLoginOptions } from './steam'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { app, BrowserWindow, ipcMain, Menu, Tray } from 'electron'
import { closeDatabase } from './db'
import { getGames, saveGames } from './db/games'
import { getGameRecords, getGameTotalPlayTime } from './db/records'
import { getCurrentUser, getUsers, saveUsers } from './db/users'
import { getMonitorStatus, initMonitor, startMonitor, stopMonitor, updateMonitorInterval } from './monitor'
import { getProcessList } from './process'
import { getInstalledGames, getSteamGamesStatus, getSteamLoginStatus, getSteamStoreAccessToken, getSteamUsers, loginSteam, logoutSteam } from './steam'

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
    show: false,
    skipTaskbar: false,
    alwaysOnTop: false,
    autoHideMenuBar: true,
    titleBarStyle: 'default',
    webPreferences: {
      preload: path.join(__dirname, 'preload.mjs'),
    },
  })

  // 开发模式下打开开发者工具
  if (!app.isPackaged) {
    // win.webContents.openDevTools({ mode: 'detach' })
  }

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
  createWindow()

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

  // 初始化数据库数据（如果是首次启动）
  // try {
  //   const users = getUsers()
  //   if (users.length === 0) {
  //     console.warn('[App] 首次启动，初始化用户数据...')
  //     const steamUsers = await getSteamUsers()
  //     if (steamUsers.length > 0) {
  //       saveUsers(steamUsers)
  //     }
  //   }

  //   const games = getGames()
  //   if (games.length === 0) {
  //     console.warn('[App] 首次启动，初始化游戏数据...')
  //     const steamGames = await getInstalledGames()
  //     if (steamGames.length > 0) {
  //       saveGames(steamGames)
  //     }
  //   }
  // }
  // catch (error) {
  //   console.error('[App] 初始化数据失败:', error)
  // }

  // 初始化监控模块
  // initMonitor()

  // 注册 IPC handlers - 进程监控
  ipcMain.handle('get-process-list', async () => {
    return await getProcessList()
  })

  // 注册 IPC handlers - Steam 相关（从数据库获取）
  ipcMain.handle('get-steam-user', async () => {
    return getCurrentUser()
  })

  ipcMain.handle('get-steam-users', async () => {
    return getUsers()
  })

  ipcMain.handle('get-steam-games', async () => {
    return getGames()
  })

  ipcMain.handle('get-steam-status', async () => {
    return await getSteamGamesStatus()
  })

  // 注册 IPC handlers - Steam 相关（重新从文件获取）
  ipcMain.handle('refresh-steam-users', async () => {
    const users = await getSteamUsers()
    saveUsers(users)
    return users
  })

  ipcMain.handle('refresh-steam-games', async () => {
    const games = await getInstalledGames()
    saveGames(games)
    return games
  })

  // 注册 IPC handlers - 游戏记录
  ipcMain.handle('get-game-records', async (_, appId?: string, limit?: number) => {
    return getGameRecords(appId, limit)
  })

  ipcMain.handle('get-game-total-playtime', async (_, appId: string) => {
    return getGameTotalPlayTime(appId)
  })

  // 注册 IPC handlers - 监控相关
  ipcMain.handle('start-monitor', async (_, intervalSeconds: number) => {
    startMonitor(intervalSeconds)
    return getMonitorStatus()
  })

  ipcMain.handle('stop-monitor', async () => {
    stopMonitor()
    return getMonitorStatus()
  })

  ipcMain.handle('update-monitor-interval', async (_, intervalSeconds: number) => {
    updateMonitorInterval(intervalSeconds)
    return getMonitorStatus()
  })

  ipcMain.handle('get-monitor-status', async () => {
    return getMonitorStatus()
  })

  // 注册 IPC handlers - Steam 认证相关
  ipcMain.handle('steam-login', async (_, options: SteamLoginOptions) => {
    try {
      const success = await loginSteam(options)
      return { success, error: null }
    }
    catch (error: any) {
      return { success: false, error: error.message }
    }
  })

  ipcMain.handle('steam-logout', async () => {
    logoutSteam()
    return { success: true }
  })

  ipcMain.handle('steam-get-login-status', async () => {
    return getSteamLoginStatus()
  })

  ipcMain.handle('steam-get-store-token', async () => {
    return await getSteamStoreAccessToken()
  })

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow()
    }
  })
})

// 应用退出前关闭数据库
app.on('before-quit', () => {
  stopMonitor()
  closeDatabase()
})
