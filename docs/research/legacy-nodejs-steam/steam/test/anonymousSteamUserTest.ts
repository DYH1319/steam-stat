import SteamUser from 'steam-user'

const steamAnonymousUser = new SteamUser({})
steamAnonymousUser.logOn({ anonymous: true })
steamAnonymousUser.on('loggedOn', () => {
  console.warn('[SteamUser] 匿名 SteamUser 已登录:', steamAnonymousUser?.steamID?.getSteamID64())

  steamAnonymousUser.getProductInfo([730], [], false, (err: any, apps: any, unknownApps: any) => {
    if (err) {
      console.error('[SteamUser] 获取产品信息失败:', err)
      return
    }
    // eslint-disable-next-line no-console
    console.dir(apps, { depth: null, maxArrayLength: null, colors: true })
    console.warn('[SteamUser] 获取未知产品信息:', unknownApps)
  })
})
