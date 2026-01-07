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

/**
 * 写入或更新 HKEY_CURRENT_USER\Software\Valve\Steam 注册表
 * @param key 注册表键名
 * @param value 注册表值
 * @param valueType 值类型（默认为 REG_SZ 字符串类型）
 * @returns 是否成功
 */
export async function writeSteamReg(
  key: string,
  value: string | number,
  valueType: 'REG_SZ' | 'REG_DWORD' = 'REG_SZ',
): Promise<boolean> {
  const steamRegPath = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam' })

  return new Promise((resolve) => {
    // 将值转换为字符串格式
    let regValue: string
    let regType: string

    if (valueType === 'REG_DWORD') {
      // DWORD 类型：将数字转换为字符串
      regValue = typeof value === 'number' ? value.toString() : value
      regType = Winreg.REG_DWORD
    }
    else {
      // REG_SZ 字符串类型
      regValue = typeof value === 'string' ? value : value.toString()
      regType = Winreg.REG_SZ
    }

    // 先检查键是否存在
    steamRegPath.get(key, (err) => {
      if (err) {
        // 键不存在，创建新键
        console.warn(`[LocalReg] 注册表键 ${key} 不存在，创建新键`)
        steamRegPath.set(key, regType, regValue, (setErr) => {
          if (setErr) {
            console.error(`[LocalReg] 创建注册表键 ${key} 失败:`, setErr)
            resolve(false)
          }
          else {
            console.warn(`[LocalReg] 成功创建注册表键 ${key} = ${regValue} (${valueType})`)
            resolve(true)
          }
        })
      }
      else {
        // 键已存在，更新值
        console.warn(`[LocalReg] 注册表键 ${key} 已存在，更新值`)
        steamRegPath.set(key, regType, regValue, (setErr) => {
          if (setErr) {
            console.error(`[LocalReg] 更新注册表键 ${key} 失败:`, setErr)
            resolve(false)
          }
          else {
            console.warn(`[LocalReg] 成功更新注册表键 ${key} = ${regValue} (${valueType})`)
            resolve(true)
          }
        })
      }
    })
  })
}

// Test
// (async () => console.warn(await readSteamReg()))();
// (async () => console.warn(await readSteamActiveProcessReg()))()
// (async () => console.warn(await readRunningAppsReg()))()
// (async () => console.warn(await writeSteamReg('AutoLoginUser', 'dyhjyh', 'REG_SZ')))()
// (async () => console.warn(await writeSteamReg('RememberPassword', 1, 'REG_DWORD')))()
