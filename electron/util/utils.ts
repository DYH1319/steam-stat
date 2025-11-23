import { spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import SteamID from 'steamid'

export function hexToDecNum(val: string | undefined): number {
  if (val && /^0x/i.test(val)) {
    return Number.parseInt(val, 16)
  }
  return Number.parseInt(val ?? '0')
}

export function hexToDecStr(val: string | undefined): string {
  return hexToDecNum(val)?.toString()
}

export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB', 'ZB', 'YB']
  const index = Math.floor(Math.log(bytes) / Math.log(1024))
  return `${(bytes / 1024 ** index).toFixed(2)} ${units[index]}`
}

export function getRegValue(regKey: Winreg.Registry, name: string): Promise<string | undefined> {
  return new Promise((resolve) => {
    regKey.get(name, (err, item) => {
      if (err || !item) {
        return resolve(undefined)
      }
      resolve(item.value)
    })
  })
}

export function steamIdToAccountId(steamId: bigint | string): number {
  if (steamId === 0n || steamId === '0') {
    return 0
  }
  return new SteamID(steamId).accountid
}

export function accountIdToSteamId(accountId: number): bigint {
  if (accountId === 0) {
    return 0n
  }
  return SteamID.fromIndividualAccountID(accountId).getBigIntID()
}

export function accountIdToSteamIdStr(accountId: number): string {
  if (accountId === 0) {
    return '0'
  }
  return SteamID.fromIndividualAccountID(accountId).getSteamID64()
}

/**
 * 运行命令行程序并捕获输出
 */
export function runCommand(command: string, args: string[], cwd: string): Promise<{ success: boolean, output: string, error: string }> {
  return new Promise((resolve) => {
    let output = ''
    let error = ''

    const process = spawn(command, args, {
      cwd,
      shell: true,
    })

    process.stdout?.on('data', (data) => {
      output += data.toString()
    })

    process.stderr?.on('data', (data) => {
      error += data.toString()
    })

    process.on('close', (code) => {
      resolve({
        success: code === 0,
        output,
        error,
      })
    })

    process.on('error', (err) => {
      resolve({
        success: false,
        output,
        error: err.message,
      })
    })
  })
}

/**
 * 获取 C# 解析器路径（兼容 dev 和打包后的环境）
 */
export function getCSharpParserPath(): { exePath: string, exeDir: string } {
  // 检测是否在 Electron 环境中
  let isElectron = false
  let isPackaged = false

  try {
    // 尝试检测 Electron 环境
    isElectron = process.versions && 'electron' in process.versions
    if (isElectron) {
      // 动态导入 electron 模块
      // eslint-disable-next-line ts/no-require-imports
      const { app } = require('electron')
      isPackaged = app.isPackaged
    }
  }
  catch {
    // 非 Electron 环境，使用开发路径
  }

  if (!isElectron || !isPackaged) {
    // 开发环境或非 Electron 环境：使用项目源码路径
    const exeDir = path.join(process.cwd(), 'electron', 'csharp', 'SteamAppInfoAndPackageInfoParser')
    const exePath = path.join(exeDir, 'SteamAppInfoParser.exe')
    return { exePath, exeDir }
  }
  else {
    // 生产环境：使用打包后的资源路径
    // eslint-disable-next-line ts/no-require-imports
    const { app } = require('electron')
    const exeDir = path.join(app.getPath('exe'), '..', 'resources', 'csharp', 'SteamAppInfoAndPackageInfoParser')
    const exePath = path.join(exeDir, 'SteamAppInfoParser.exe')
    return { exePath, exeDir }
  }
}

/**
 * 递归计算文件夹大小（字节）
 * @param dirPath 文件夹路径
 * @returns 文件夹总大小（字节），如果路径不存在返回 0n
 */
export function calculateDirectorySize(dirPath: string): bigint {
  let totalSize = 0n

  try {
    // 检查路径是否存在
    if (!fs.existsSync(dirPath)) {
      return 0n
    }

    // 获取路径信息
    const stats = fs.statSync(dirPath)

    // 如果是文件，直接返回文件大小
    if (stats.isFile()) {
      return BigInt(stats.size)
    }

    // 如果是文件夹，递归计算
    if (stats.isDirectory()) {
      const files = fs.readdirSync(dirPath)

      for (const file of files) {
        const filePath = path.join(dirPath, file)
        try {
          const fileStats = fs.statSync(filePath)

          if (fileStats.isFile()) {
            totalSize += BigInt(fileStats.size)
          }
          else if (fileStats.isDirectory()) {
            // 递归计算子文件夹大小
            totalSize += calculateDirectorySize(filePath)
          }
        }
        catch (error) {
          // 跳过无法访问的文件/文件夹（权限问题、符号链接等）
          console.warn(`[calculateDirectorySize] 跳过文件: ${filePath}`, error)
        }
      }
    }
  }
  catch (error) {
    console.error(`[calculateDirectorySize] 计算文件夹大小失败: ${dirPath}`, error)
  }

  return totalSize
}
