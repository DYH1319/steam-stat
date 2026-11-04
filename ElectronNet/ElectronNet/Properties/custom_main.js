/**
 * ElectronNET custom_main.js
 * 用于在 Electron 启动前注册自定义协议
 *
 * 参考文档: https://github.com/ElectronNET/Electron.NET/wiki/Custom_main
 */

module.exports.onStartup = function (_host) {
  const { app, protocol, net } = require('electron')
  const fs = require('node:fs')
  const path = require('node:path')

  // 修复 Windows 11 frameless 窗口圆角变直角的 Chromium bug
  // https://github.com/electron/electron/issues/48340
  // app.commandLine.appendSwitch('disable-gpu-compositing')

  if (process.platform === 'win32') {
    // Windows 原生通知需要与安装快捷方式一致的 AppUserModelID（electron-builder appId）。
    // 开发模式跑的是 node_modules 里的 electron.exe（默认图标），若与发行版共用同一个 ID，
    // 任务栏会按 ID 缓存到 Electron 默认图标，因此开发模式使用独立 ID
    const appId = app.isPackaged ? 'asia.dyh1319.steamstat' : 'asia.dyh1319.steamstat.dev'
    app.setAppUserModelId(appId)

    // DotNet-First 布局：<安装目录>\Steam Stat.exe 为 .NET 宿主，Electron 位于 <安装目录>\electron\。
    // 显式写入窗口的任务栏属性：图标取 .NET 宿主（与托盘同一图标），
    // 固定到任务栏后的重新启动也走 .NET 宿主（直接启动 electron 子目录里的 exe 无法运行）
    const hostExe = path.join(path.dirname(process.execPath), '..', path.basename(process.execPath))
    if (app.isPackaged && fs.existsSync(hostExe)) {
      app.on('browser-window-created', (_event, window) => {
        try {
          window.setAppDetails({
            appId,
            appIconPath: hostExe,
            appIconIndex: 0,
            relaunchCommand: `"${hostExe}"`,
            relaunchDisplayName: 'Steam Stat',
          })
        }
        catch (error) {
          console.error('[Custom Main JS] setAppDetails failed:', error)
        }
      })
    }
  }

  app.on('ready', () => {
    // 使用 protocol.handle() 注册协议处理器（Electron 25+ 推荐方式）
    protocol.handle('steam-stat-file', (request) => {
      const url = request.url.slice('steam-stat-file://'.length)
      try {
        // 解码 URL 编码的路径
        const decodedPath = decodeURIComponent(url)
        // console.warn('[Steam Stat File Protocol] Loading:', decodedPath)
        // 使用 net.fetch 获取本地文件
        return net.fetch(`file://${decodedPath}`)
      }
      catch (error) {
        console.error('[Steam Stat File Protocol Error]:', error)
        return new Response('Not Found', { status: 404 })
      }
    })
    console.warn('[Custom Main JS] steam-stat-file protocol registered.')
  })

  return true
}
