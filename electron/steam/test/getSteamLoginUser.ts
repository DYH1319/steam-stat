import fs from 'node:fs'
import path from 'node:path'
import SteamID from 'steamid'
import Winreg from 'winreg'
import { getRegValue, hexToDecNum, hexToDecStr } from '../../util/utils'

interface LoginUserInfo {
  AccountID: number
  SteamID: bigint
  Steam2ID: string
  Steam3ID: string
  Steam64ID: string
  AccountName: string
  PersonaName: string
  RememberPassword: number
  WantsOfflineMode: number
  SkipOfflineModeWarning: number
  AllowAutoLogin: number
  MostRecent: number
  Timestamp: number
}

interface SteamLoginUserInfo {
  RunningAppID?: number
  AutoLoginUser?: string
  LastGameNameUsed?: string
  RememberPassword?: number | string
  ActiveUser?: number
  ActiveUserSteamID?: bigint
  ActiveUserSteam2ID?: string
  ActiveUserSteam3ID?: string
  ActiveUserSteam64ID?: string
  loginusers?: Record<string, LoginUserInfo>
}

/**
 * 获取在本机登录过以及目前登录的 Steam 账号信息
 */
async function getSteamLoginUserInfo(): Promise<SteamLoginUserInfo> {
  // 注册表 Steam 路径
  const steamBasePath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam' })
  const activeProcessPath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam\\ActiveProcess' })
  // 读取注册表数据
  const [
    RunningAppID,
    AutoLoginUser,
    LastGameNameUsed,
    RememberPassword,
    ActiveUser,
  ] = await Promise.all([
    getRegValue(steamBasePath, 'RunningAppID'),
    getRegValue(steamBasePath, 'AutoLoginUser'),
    getRegValue(steamBasePath, 'LastGameNameUsed'),
    getRegValue(steamBasePath, 'RememberPassword'),
    getRegValue(activeProcessPath, 'ActiveUser'),
  ])

  // 尝试获取 SteamPath
  const SteamPath = await getRegValue(steamBasePath, 'SteamPath')
  let loginusers: Record<string, LoginUserInfo> = {}
  if (SteamPath) {
    const vdfPath = path.join(SteamPath, 'config', 'loginusers.vdf')
    if (fs.existsSync(vdfPath)) {
      const vdf = await import('@node-steam/vdf')
      const txt = fs.readFileSync(vdfPath, 'utf-8')
      loginusers = vdf.parse(txt).users

      Object.keys(loginusers).forEach((key) => {
        const sid = new SteamID(key)
        loginusers[key] = {
          ...loginusers[key],
          AccountID: Number.parseInt(sid.getSteam3RenderedID().replace('[', '').replace(']', '').split(':')[2]),
          SteamID: sid.getBigIntID(),
          Steam2ID: sid.getSteam2RenderedID(true),
          Steam3ID: sid.getSteam3RenderedID(),
          Steam64ID: sid.getSteamID64(),
        }
      })
    }
  }

  const sid = SteamID.fromIndividualAccountID(hexToDecStr(ActiveUser))
  const activeUser = hexToDecNum(ActiveUser)

  return {
    RunningAppID: hexToDecNum(RunningAppID),
    AutoLoginUser,
    LastGameNameUsed,
    RememberPassword: hexToDecNum(RememberPassword),
    ActiveUser: activeUser,
    ActiveUserSteamID: activeUser !== 0 ? sid.getBigIntID() : undefined,
    ActiveUserSteam2ID: activeUser !== 0 ? sid.getSteam2RenderedID(true) : undefined,
    ActiveUserSteam3ID: activeUser !== 0 ? sid.getSteam3RenderedID() : undefined,
    ActiveUserSteam64ID: activeUser !== 0 ? sid.getSteamID64() : undefined,
    loginusers,
  }
}

(async () => {
  console.warn('[Steam Test] 登录用户信息: ')
  // eslint-disable-next-line no-console
  console.dir(await getSteamLoginUserInfo(), { depth: 1, colors: true })
})()
