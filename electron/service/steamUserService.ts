import type { SteamLoginUserInfo, SteamLoginUsersInfo } from '../types/steamUser'
import fs from 'node:fs'
import path from 'node:path'
import SteamID from 'steamid'
import Winreg from 'winreg'
import * as steamUser from '../db/steamUser'
import { getRegValue, hexToDecNum, hexToDecStr } from '../util/utils'

/**
 * 获取在本机登录过以及目前登录的 Steam 账号信息
 */
async function getSteamLoginUserInfoFromLocalFile(): Promise<SteamLoginUsersInfo> {
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
  let loginusers: Record<string, SteamLoginUserInfo> = {}
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

/**
 * 初始化或更新 Steam 用户信息到数据库
 */
export async function initOrUpdateSteamUserDb() {
  // 读取本地文件
  const loginUserInfo = await getSteamLoginUserInfoFromLocalFile()
  return steamUser.insertOrUpdateSteamUserBatch(loginUserInfo)
}

/**
 * 获取 Steam 用户信息
 */
export async function getSteamLoginUserInfo() {
  return steamUser.getAllUsers()
}
