import fs from 'node:fs'
import path from 'node:path'
import { formatBytes } from '../../util/utils'
import { getLibraryFolders } from './getLibraryFolders'

export interface InstalledApps {
  appId: number
  name: string
  installDir: string
  gameFolder: string
  exePaths?: string[]
  sizeOnDisk?: number
  sizeOnDiskHuman?: string
  libraryFolder: string
}

/**
 * 获取本机已安装的 Steam 应用列表
 */
export async function getInstalledApps(): Promise<InstalledApps[]> {
  const libraryFolders = await getLibraryFolders()
  const apps: InstalledApps[] = []

  for (const folder of libraryFolders) {
    if (!fs.existsSync(folder)) {
      continue
    }

    const files = fs.readdirSync(folder)
    for (const file of files) {
      if (file.startsWith('appmanifest_') && file.endsWith('.acf')) {
        const manifestPath = path.join(folder, file)
        const content = fs.readFileSync(manifestPath, 'utf8')

        // 延迟加载 vdf 模块
        const vdf = await import('@node-steam/vdf')
        const data = vdf.parse(content)
        const appState = data.AppState || {}

        const appId = appState.appid
        const name = appState.name || 'Unknown'
        const installDir = appState.installdir || ''

        // 构建应用可执行文件的完整路径
        const gameFolder = path.join(folder, 'common', installDir)

        const sizeOnDisk = appState.SizeOnDisk || -1
        const sizeOnDiskHuman = sizeOnDisk > 0 ? formatBytes(sizeOnDisk) : '-1'

        // 尝试查找游戏目录下的 .exe 文件
        // let exePaths: string[] = []
        // if (fs.existsSync(gameFolder)) {
        //   try {
        //     exePaths = findExeFiles(gameFolder)
        //   }
        //   catch (e) {
        //     console.error(`[Steam] 扫描游戏目录失败 ${gameFolder}:`, e)
        //   }
        // }

        apps.push({
          appId,
          name,
          installDir,
          gameFolder,
          sizeOnDisk,
          sizeOnDiskHuman,
          libraryFolder: folder,
        })
      }
    }
  }

  return apps
}

/**
 * 获取本机已安装的 Steam 应用列表（通过注册表）。会包含所有Game、DLC等各种类型的APP，且中文乱码 + 有些Game在注册表中没有Name（例如：1260520 Patrick's Parabox）
 */
// export async function getInstalledApps2(): Promise<Array<{ appId: string, reg: { Installed?: string, Name?: string, Running?: string, Updating?: string } }>> {
//   const result: Array<{ appId: string, reg: { Installed?: string, Name?: string, Running?: string, Updating?: string } }> = []

//   // 根注册表
//   const appsRoot = new Winreg({ hive: Winreg.HKCU, key: '\\Software\\Valve\\Steam\\Apps' })
//   // 遍历全部 appId
//   const appIds: string[] = await new Promise((resolve) => {
//     appsRoot.keys((err, items) => {
//       if (err || !items) {
//         resolve([])
//       }
//       else {
//         resolve(items.map(k => k.key.split('\\').pop() || ''))
//       }
//     })
//   })

//   // 依次读取每个appId下的4个字段
//   for (const appId of appIds) {
//     if (!appId) {
//       continue
//     }
//     const regKey = new Winreg({ hive: Winreg.HKCU, key: `\\Software\\Valve\\Steam\\Apps\\${appId}` })
//     const get = (name: string): Promise<string | undefined> => {
//       return new Promise((resolve) => {
//         regKey.get(name, (err, item) => {
//           if (err || !item) {
//             resolve(undefined)
//           }
//           else {
//             resolve(item.value)
//           }
//         })
//       })
//     }
//     const [Installed, Name, Running, Updating] = await Promise.all([
//       get('Installed'),
//       get('Name'),
//       get('Running'),
//       get('Updating'),
//     ])
//     result.push({
//       appId,
//       reg: { Installed, Name, Running, Updating },
//     })
//   }
//   return result
// }

// (async () => {
//   const apps = await getInstalledApps()
//   console.warn('[Steam Test] 已安装应用数量:', apps.length)
//   console.warn('[Steam Test] 已安装应用: ')
//   // eslint-disable-next-line no-console
//   console.dir(apps, { depth: null, maxArrayLength: null, colors: true })
//   // const apps2 = await getInstalledApps2()
//   // console.warn('[Steam Test] 已安装应用2数量:', apps2.length)
//   // console.warn('[Steam Test] 已安装应用2: ')
//   // console.dir(apps2, { depth: null, maxArrayLength: null, colors: true })
// })()
