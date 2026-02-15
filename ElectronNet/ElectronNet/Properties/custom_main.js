/**
 * ElectronNET custom_main.js
 * 用于在 Electron 启动前注册自定义协议
 *
 * 参考文档: https://github.com/ElectronNET/Electron.NET/wiki/Custom_main
 */

module.exports.onStartup = function (_host) {
  const { app, protocol, net } = require('electron')

  // 修复 Windows 11 frameless 窗口圆角变直角的 Chromium bug
  // https://github.com/electron/electron/issues/48340
  // app.commandLine.appendSwitch('disable-gpu-compositing')

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
