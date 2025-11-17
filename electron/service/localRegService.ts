import type { SteamActiveProcessReg, SteamReg } from '../types/localReg'
/**
 * 本地注册表读写服务
 */
import { exec } from 'node:child_process'
import Winreg from 'winreg'

/**
 * 读取 HKEY_CURRENT_USER\Software\Valve\Steam 注册表
 */
export async function readSteamReg(): Promise<SteamReg> {
  const steamReg: SteamReg = {} as SteamReg
  const steamRegPath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam' })

  // 一次性读取所有注册表条目
  return new Promise((resolve, reject) => {
    steamRegPath.values((err, items) => {
      if (err) {
        console.error('[LocalReg] 读取 Steam 注册表失败:', err)
        reject(err)
        return
      }

      // 将所有条目放入 steamReg 对象
      items.forEach((item) => {
        const key = item.name as keyof SteamReg
        const value = item.value

        // 根据类型转换值
        if (item.type === Winreg.REG_DWORD) {
          // DWORD 类型转换为数字
          steamReg[key] = Number.parseInt(value, 16) as never
        }
        else {
          // 字符串类型直接赋值
          steamReg[key] = value as never
        }
      })

      resolve(steamReg)
    })
  })
}

/**
 * 读取 HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess 注册表
 */
export async function readSteamActiveProcessReg(): Promise<SteamActiveProcessReg> {
  const steamActiveProcessReg: SteamActiveProcessReg = {} as SteamActiveProcessReg
  const steamActiveProcessRegPath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam\\ActiveProcess' })

  // 一次性读取所有注册表条目
  return new Promise((resolve, reject) => {
    steamActiveProcessRegPath.values((err, items) => {
      if (err) {
        console.error('[LocalReg] 读取 Steam ActiveProcess 注册表失败:', err)
        reject(err)
        return
      }

      // 将所有条目放入 steamActiveProcessReg 对象
      items.forEach((item) => {
        const key = item.name as keyof SteamActiveProcessReg
        const value = item.value

        // 根据类型转换值
        if (item.type === Winreg.REG_DWORD) {
          // DWORD 类型转换为数字
          steamActiveProcessReg[key] = Number.parseInt(value, 16) as never
        }
        else {
          // 字符串类型直接赋值
          steamActiveProcessReg[key] = value as never
        }
      })

      resolve(steamActiveProcessReg)
    })
  })
}

/**
 * 读取 HKEY_CURRENT_USER\Software\Valve\Steam\Apps\{appId} 注册表
 * @returns 运行的 appId 列表
 */
export async function readRunningAppsReg(): Promise<number[]> {
  // 查询所有子项及属性
  const regPath = 'HKCU\\Software\\Valve\\Steam\\Apps'
  const cmd = `reg query "${regPath}" /s`
  const stdout: string = await new Promise((resolve) => {
    exec(cmd, { encoding: 'utf8' }, (err, out) => {
      if (err) {
        resolve('')
      }
      else {
        resolve(out || '')
      }
    })
  })
  // 匹配每个 appId
  // 使用负向前瞻确保不会跨越下一个 appId 边界
  const regex = /HKEY_CURRENT_USER\\Software\\Valve\\Steam\\Apps\\(\d{1,12})(?:(?!HKEY_CURRENT_USER\\Software\\Valve\\Steam\\Apps)[\s\S])*?Running\s+REG_DWORD\s+(.?0x[\da-fA-F]+|\d+)/g
  // find all matches，立即提取捕获组避免保存完整的match对象（包含input）
  const matches = Array.from(stdout.matchAll(regex), m => [m[1], m[2]])
  return matches
    .map(([appIdStr, running]) => {
      const appId = Number.parseInt(appIdStr)
      if (Number.parseInt(running, 16) !== 0) {
        return appId
      }
      return undefined
    })
    .filter(Boolean) as number[]
}

// Test
// (async () => console.warn(await readSteamReg()))();
// (async () => console.warn(await readSteamActiveProcessReg()))()
(async () => console.warn(await readRunningAppsReg()))()
