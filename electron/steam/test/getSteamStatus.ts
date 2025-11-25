import Winreg from 'winreg'
import { getRegValue, hexToDecNum } from '../../util/utils'

export interface SteamStatus {
  SteamPath?: string
  SteamExe?: string
  pid?: string | number
  SteamClientDll?: string
  SteamClientDll64?: string
}

/**
 * 获取本机 Steam 相关信息
 */
export async function getSteamStatus(): Promise<SteamStatus> {
  // 读取 HKEY_CURRENT_USER\Software\Valve\Steam
  const steamBasePath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam' })
  const steamBase = Promise.all([
    getRegValue(steamBasePath, 'SteamPath'),
    getRegValue(steamBasePath, 'SteamExe'),
  ])
  // 读取 HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess
  const activeProcessPath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam\\ActiveProcess' })
  const activeProcess = Promise.all([
    getRegValue(activeProcessPath, 'pid'),
    getRegValue(activeProcessPath, 'SteamClientDll'),
    getRegValue(activeProcessPath, 'SteamClientDll64'),
  ])
  const [[SteamPath, SteamExe], [pid, SteamClientDll, SteamClientDll64]] = await Promise.all([steamBase, activeProcess])
  return {
    SteamPath,
    SteamExe,
    pid: hexToDecNum(pid),
    SteamClientDll,
    SteamClientDll64,
  }
}

(async () => console.warn('[Steam Test] 本机 Steam 相关信息: ', await getSteamStatus()))()
