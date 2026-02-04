/**
 * ElectronNET custom_main.js
 * 用于在 Electron 启动前注册自定义协议
 *
 * 参考文档: https://github.com/ElectronNET/Electron.NET/wiki/Custom_main
 */

module.exports.onStartup = function (_host) {
  const { app, protocol, net } = require('electron')

  // 必须在 app.ready 之前注册 scheme
  // 这样才能让协议具有 secure 和 supportFetchAPI 特权
  protocol.registerSchemesAsPrivileged([
    {
      scheme: 'steam-avatar',
      privileges: {
        secure: true,
        supportFetchAPI: true,
        standard: true,
        allowServiceWorkers: false,
        corsEnabled: true,
      },
    },
  ])

  app.on('ready', () => {
    // 使用 protocol.handle() 注册协议处理器（Electron 25+ 推荐方式）
    protocol.handle('steam-avatar', (request) => {
      const url = request.url.slice('steam-avatar://'.length)
      try {
        // 解码 URL 编码的路径
        const decodedPath = decodeURIComponent(url)
        // 使用 net.fetch 获取本地文件
        return net.fetch(`file://${decodedPath}`)
      }
      catch (error) {
        console.error('[Protocol] steam-avatar error:', error)
        return new Response('Not Found', { status: 404 })
      }
    })
    console.warn('[Steam Stat Custom Main JS] steam-avatar protocol registered.')
  })

  return true
}
