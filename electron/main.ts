import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { app, BrowserWindow } from 'electron'

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

process.env.DIST = path.join(__dirname, '../dist')
process.env.VITE_PUBLIC = app.isPackaged ? process.env.DIST : path.join(process.env.DIST, '../public')

let win: BrowserWindow | null

const VITE_DEV_SERVER_URL = process.env.VITE_DEV_SERVER_URL

function createWindow() {
  win = new BrowserWindow({
    width: 1920,
    height: 1080,
    icon: path.join(process.env.VITE_PUBLIC || '', 'favicon.svg'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.mjs'),
    },
  })

  // 开发模式下打开开发者工具
  if (!app.isPackaged) {
    // win.webContents.openDevTools()
  }

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
    win = null
  }
})

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) {
    createWindow()
  }
})

app.whenReady().then(createWindow)
