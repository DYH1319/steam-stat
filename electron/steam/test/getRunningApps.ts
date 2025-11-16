import { exec } from 'node:child_process'
import Winreg from 'winreg'
import { hexToDecNum } from '../../util/utils'
import { getInstalledApps } from './getInstalledApps'

export interface RunningApps {
  appId: number
  name: string
}

/**
 * @deprecated
 *
 * 获取本机正在运行的 Steam 应用列表
 *
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningApps#1: 3.226s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningApps#2: 2.630s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningApps#3: 2.616s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningApps#4: 2.633s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningApps#5: 2.591s
 */
export async function getRunningApps(): Promise<RunningApps[]> {
  const regKeyCache = new Map<number, Winreg.Registry>()
  function getRegKey(appId: number): Winreg.Registry {
    if (!regKeyCache.has(appId)) {
      regKeyCache.set(appId, new Winreg({ hive: Winreg.HKCU, key: `\\Software\\Valve\\Steam\\Apps\\${appId}` }))
    }
    return regKeyCache.get(appId)!
  }

  function getRegValue(regKey: Winreg.Registry, name: string): Promise<string | undefined> {
    return new Promise((resolve) => {
      regKey.get(name, (err, item) => {
        if (err || !item) {
          return resolve(undefined)
        }
        resolve(item.value)
      })
    })
  }

  const installedApps = await getInstalledApps()
  const runningApps: RunningApps[] = []
  for (const app of installedApps) {
    const regKey = getRegKey(app.appId)
    const reg = await getRegValue(regKey, 'Running')
    if (reg && hexToDecNum(reg) !== 0) {
      runningApps.push({ appId: app.appId, name: app.name })
    }
  }
  return runningApps
}

/**
 * @deprecated
 *
 * 批量全量keys/values查询实现
 *
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegistryAll#1: 2.047s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegistryAll#2: 1.203s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegistryAll#3: 1.182s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegistryAll#4: 1.203s
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegistryAll#5: 1.201s
 */
export async function getRunningAppsByRegistryAll(): Promise<RunningApps[]> {
  const appsRoot = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam\\Apps' })
  const installedApps = await getInstalledApps()
  // 建立 appId->name 映射
  const nameMap = new Map(installedApps.map(a => [a.appId, a.name]))
  const subKeys: Winreg.Registry[] = await new Promise((resolve) => {
    appsRoot.keys((err, items) => {
      if (err || !items) {
        resolve([])
      }
      else {
        const mapped = items.map(k => k)
        resolve(mapped)
      }
    })
  })
  // Promise.all values
  const fieldsList = await Promise.all(subKeys.map(k => new Promise<{ appId: number, Running?: string }>((resolve) => {
    k.values((e, items) => {
      if (e || !items) {
        resolve({ appId: Number.parseInt(k.key.split('\\').pop() || '0') })
        return
      }
      const map = Object.fromEntries(items.map(i => [i.name, i.value]))
      resolve({ appId: Number.parseInt(k.key.split('\\').pop() || '0'), Running: map.Running })
    })
  })))
  return fieldsList.filter(f => f.appId && f.Running && hexToDecNum(f.Running) !== 0)
    .map(f => ({ appId: f.appId, name: nameMap.get(f.appId) || '' }))
}

/**
 * 利用 reg query 命令子进程实现
 *
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegQuery#1: 429.294ms
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegQuery#2: 436.217ms
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegQuery#3: 430.842ms
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegQuery#4: 440.666ms
 * [Steam Test] 本机正在运行的 Steam 应用列表:  [ { appId: 378110, name: 'Hack RUN' } ]
 * getRunningAppsByRegQuery#5: 429.093ms
 */
export async function getRunningAppsByRegQuery(): Promise<RunningApps[]> {
  const installedApps = await getInstalledApps()
  const nameMap = new Map(installedApps.map(a => [a.appId, a.name]))
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
  // 分析结果：每个 Key Name    Type    Data
  // 匹配每个 appId
  const regex = /HKEY_CURRENT_USER\\Software\\Valve\\Steam\\Apps\\(\d{1,12})[\s\S]*?Running\s+REG_DWORD\s+(.?0x[\da-fA-F]+|\d+)/g
  // find all matches
  const matches = Array.from(stdout.matchAll(regex))
  return matches
    .map((m) => {
      const running = m[2]
      const appId = Number.parseInt(m[1])
      if (hexToDecNum(running) !== 0) {
        return { appId, name: nameMap.get(appId) || '' }
      }
      return undefined
    })
    .filter(Boolean) as RunningApps[]
}

(async () => {
  // 性能测试
  // for (let i = 1; i <= 5; i++) {
  //   console.time(`getRunningApps#${i}`)
  //   console.warn('[Steam Test] 本机正在运行的 Steam 应用列表: ', await getRunningApps())
  //   console.timeEnd(`getRunningApps#${i}`)
  // }

  // for (let i = 1; i <= 5; i++) {
  //   console.time(`getRunningAppsByRegistryAll#${i}`)
  //   console.warn('[Steam Test] 本机正在运行的 Steam 应用列表: ', await getRunningAppsByRegistryAll())
  //   console.timeEnd(`getRunningAppsByRegistryAll#${i}`)
  // }

  // for (let i = 1; i <= 5; i++) {
  //   console.time(`getRunningAppsByRegQuery#${i}`)
  //   console.warn('[Steam Test] 本机正在运行的 Steam 应用列表: ', await getRunningAppsByRegQuery())
  //   console.timeEnd(`getRunningAppsByRegQuery#${i}`)
  // }

  console.warn('[Steam Test] 本机正在运行的 Steam 应用列表: ', await getRunningAppsByRegQuery())
})()
