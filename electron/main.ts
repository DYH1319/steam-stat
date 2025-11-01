import { exec } from 'node:child_process'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'
import { app, BrowserWindow, ipcMain, Menu, Tray } from 'electron'

const execAsync = promisify(exec)

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
    win.webContents.openDevTools()
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

// 获取进程列表
async function getProcessList() {
  try {
    console.warn('[进程监控] 开始获取进程列表...')
    // Windows 中文系统使用 GBK 编码，通过 chcp 65001 切换到 UTF-8
    const { stdout } = await execAsync('chcp 65001 > nul && tasklist /FO CSV /NH', { encoding: 'utf8' })
    console.warn('[进程监控] tasklist 命令执行成功')
    console.warn('[进程监控] stdout 前 500 个字符:', stdout.substring(0, 500))

    const lines = stdout.trim().split('\n').filter(line => line.trim().length > 0)
    console.warn('[进程监控] 总行数:', lines.length)

    const processes = lines.map((line) => {
      // 移除行尾的 \r
      const cleanLine = line.trim()
      const parts = cleanLine.match(/"([^"]*)"/g)
      if (!parts || parts.length < 5) {
        console.warn('[进程监控] 跳过无效行:', cleanLine.substring(0, 100))
        return null
      }

      return {
        name: parts[0].replace(/"/g, ''),
        pid: Number.parseInt(parts[1].replace(/"/g, ''), 10),
        sessionName: parts[2].replace(/"/g, ''),
        sessionNumber: parts[3].replace(/"/g, ''),
        memUsage: parts[4].replace(/"/g, ''),
      }
    }).filter(Boolean)

    console.warn('[进程监控] 解析后的进程数量:', processes.length)
    console.warn('[进程监控] 前 5 个进程:', processes.slice(0, 5))

    return processes
  }
  catch (error) {
    console.error('[进程监控] 获取进程列表失败:', error)
    return []
  }
}

app.whenReady().then(() => {
  createWindow()

  // 注册 IPC handlers
  ipcMain.handle('get-process-list', async () => {
    console.warn('[进程监控] 收到前端请求获取进程列表')
    const processes = await getProcessList()
    console.warn('[进程监控] 返回给前端的进程数量:', processes.length)
    return processes
  })

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow()
    }
  })
})
