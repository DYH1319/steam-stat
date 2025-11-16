/**
 * appinfo.vdf 测试和调试工具
 */

import { spawn } from 'node:child_process'
import path from 'node:path'
import { parseAppInfo } from './appinfo'
import { getSteamStatus } from './test/getSteamStatus'

/**
 * 安全地测试解析 appinfo.vdf
 */
export async function testParseAppInfo() {
  try {
    console.warn('[AppInfo Test] 开始测试...')

    const { SteamPath: steamPath } = await getSteamStatus()
    if (!steamPath) {
      console.error('[AppInfo Test] 未找到 Steam 安装路径')
      return
    }

    const appinfoPath = path.join(steamPath, 'appcache', 'appinfo.vdf')
    console.warn('[AppInfo Test] 文件路径:', appinfoPath)

    // 检查文件是否存在
    const fs = await import('node:fs')
    if (!fs.existsSync(appinfoPath)) {
      console.error('[AppInfo Test] 文件不存在:', appinfoPath)
      return
    }

    // 获取文件大小
    const stats = fs.statSync(appinfoPath)
    console.warn(`[AppInfo Test] 文件大小: ${(stats.size / 1024 / 1024).toFixed(2)} MB`)

    // 尝试解析
    console.warn('[AppInfo Test] 开始解析...')
    const apps = parseAppInfo(appinfoPath)

    console.warn(`[AppInfo Test] 解析成功！共 ${apps.length} 个应用`)

    // 显示前几个应用
    if (apps.length > 0) {
      // 调试：查看第一个应用的原始数据
      if (apps[0]) {
        console.warn('\n[AppInfo Debug] 第二个应用的数据:')
        console.warn('  AppID:', apps[1].appid)
        console.warn('  Data:', apps[1].data)
      }

      console.warn('\n[AppInfo Test] 示例应用 (前 20 个 AppID):')
      apps.slice(0, 20).forEach((app, index) => {
        const gameName = app.data?.name || '(未知)'
        const gameType = app.data?.type || ''
        console.warn(`  ${index + 1}. AppID: ${app.appid} - ${gameName} (${gameType})`)
      })

      // 统计一些常见的 Steam AppID
      const knownApps = new Map([
        [730, 'Counter-Strike 2'],
        [440, 'Team Fortress 2'],
        [570, 'Dota 2'],
        [271590, 'Grand Theft Auto V'],
        [1172470, 'Apex Legends'],
        [1938090, 'Call of Duty®: HQ'],
      ])

      console.warn('\n[AppInfo Test] 检查已知游戏:')
      knownApps.forEach((name, appId) => {
        const found = apps.find(app => app.appid === appId)
        if (found) {
          console.warn(`  ✓ [${appId}] ${name}`)
        }
        else {
          console.warn(`  ✗ [${appId}] ${name} (未找到)`)
        }
      })

      // 显示 AppID 范围
      const appIds = apps.map(app => app.appid).sort((a, b) => a - b)
      console.warn(`\n[AppInfo Test] AppID 范围: ${appIds[0]} - ${appIds[appIds.length - 1]}`)
    }

    // 导出为 JSON 文件
    await exportToJSON(apps)

    return apps
  }
  catch (error) {
    console.error('[AppInfo Test] 测试失败:', error)
    if (error instanceof Error) {
      console.error('[AppInfo Test] 错误堆栈:', error.stack)
    }
    return null
  }
}

/**
 * 导出应用信息为 JSON 文件
 */
async function exportToJSON(apps: any[]) {
  try {
    const fs = await import('node:fs')
    const { app } = await import('electron')

    // 保存到用户数据目录
    const userDataPath = app.getPath('userData')
    const outputPath = path.join(userDataPath, 'appinfo.json')

    // 使用流式写入，避免栈溢出
    const stream = fs.createWriteStream(outputPath, 'utf8')
    stream.write('[\n')

    for (let i = 0; i < apps.length; i++) {
      const appInfo = apps[i]

      // 转换为可序列化的格式（简化版本，避免过深嵌套）
      const jsonItem = {
        appid: appInfo.appid,
        infoState: appInfo.infoState,
        lastUpdated: appInfo.lastUpdated.toISOString(),
        token: appInfo.token.toString(),
        hash: appInfo.hash.toString('hex'),
        binaryDataHash: appInfo.binaryDataHash?.toString('hex'),
        changeNumber: appInfo.changeNumber,
        name: appInfo.data?.name,
        type: appInfo.data?.type,
        oslist: appInfo.data?.oslist,
        developer: appInfo.data?.developer,
        publisher: appInfo.data?.publisher,
        // 保存完整数据（限制深度）
        raw: safeStringify(appInfo.data?.raw, 10),
      }

      stream.write('  ')
      stream.write(JSON.stringify(jsonItem))
      if (i < apps.length - 1) {
        stream.write(',\n')
      }
      else {
        stream.write('\n')
      }
    }

    stream.write(']')
    stream.end()

    // 等待写入完成
    await new Promise<void>((resolve) => {
      stream.on('finish', () => resolve())
    })

    console.warn(`\n[AppInfo Export] 已导出 ${apps.length} 个应用到:`)
    console.warn(`  ${outputPath}`)
    console.warn(`  文件大小: ${(fs.statSync(outputPath).size / 1024 / 1024).toFixed(2)} MB`)
  }
  catch (error) {
    console.error('[AppInfo Export] 导出失败:', error)
  }
}

/**
 * 安全的 JSON 序列化，限制深度避免栈溢出
 */
function safeStringify(obj: any, maxDepth: number): any {
  if (maxDepth <= 0) {
    return '[深度限制]'
  }

  if (obj === null || obj === undefined) {
    return obj
  }

  if (typeof obj !== 'object') {
    return obj
  }

  if (Array.isArray(obj)) {
    return obj.map(item => safeStringify(item, maxDepth - 1))
  }

  const result: any = {}
  for (const key in obj) {
    if (Object.prototype.hasOwnProperty.call(obj, key)) {
      result[key] = safeStringify(obj[key], maxDepth - 1)
    }
  }
  return result
}

/**
 * 导出文件头信息（用于调试）
 */
export async function debugAppInfoHeader() {
  try {
    const { SteamPath: steamPath } = await getSteamStatus()
    if (!steamPath) {
      return
    }

    const appinfoPath = path.join(steamPath, 'appcache', 'appinfo.vdf')
    const fs = await import('node:fs')

    if (!fs.existsSync(appinfoPath)) {
      console.error('[AppInfo Debug] 文件不存在')
      return
    }

    const buf = fs.readFileSync(appinfoPath)

    // 输出文件头信息
    console.warn('[AppInfo Debug] 文件头 (前 128 字节):')
    console.warn(buf.subarray(0, 128).toString('hex'))

    console.warn('\n[AppInfo Debug] 前 4 个字节 (魔术字节):')
    console.warn(`  Hex: ${buf.subarray(0, 4).toString('hex')}`)
    console.warn(`  UInt32LE: 0x${buf.readUInt32LE(0).toString(16)}`)
    console.warn(`  Bytes: [${buf[0]}, ${buf[1]}, ${buf[2]}, ${buf[3]}]`)

    console.warn('\n[AppInfo Debug] 字节 4-8 (版本号):')
    console.warn(`  UInt32LE: ${buf.readUInt32LE(4)}`)
  }
  catch (error) {
    console.error('[AppInfo Debug] 调试失败:', error)
  }
}

/**
 * 运行 C# SteamAppInfoParser 并对比结果
 */
export async function runCSharpParserAndCompare() {
  try {
    console.warn('\n[C# Parser] ==================== 开始运行 C# 解析器 ====================')

    const { SteamPath: steamPath } = await getSteamStatus()
    if (!steamPath) {
      console.error('[C# Parser] 未找到 Steam 安装路径')
      return
    }

    const appinfoPath = path.join(steamPath, 'appcache', 'appinfo.vdf')
    const csharpExePath = path.join(process.cwd(), 'electron', 'csharp', 'SteamAppInfoAndPackageInfoParser', 'SteamAppInfoParser.exe')

    // 检查 exe 文件是否存在
    const fs = await import('node:fs')
    if (!fs.existsSync(csharpExePath)) {
      console.error('[C# Parser] ❌ 未找到 C# 程序:', csharpExePath)
      console.error('[C# Parser] 请确保已将编译好的 exe 文件放在 electron/csharp/SteamAppInfoAndPackageInfoParser/ 目录')
      return
    }

    console.warn('[C# Parser] C# 程序路径:', csharpExePath)
    console.warn('[C# Parser] appinfo.vdf 路径:', appinfoPath)

    // 运行程序
    console.warn('\n[C# Parser] 运行解析器...')
    const startTime = Date.now()
    const exeDir = path.dirname(csharpExePath)
    const run = await runCommand(csharpExePath, [`"${appinfoPath}"`], exeDir)
    const duration = Date.now() - startTime

    if (!run.success) {
      console.error('[C# Parser] ❌ 运行失败')
      console.error(run.error)
      return
    }

    console.warn('[C# Parser] ✅ 运行完成')
    console.warn('[C# Parser] 耗时:', duration, 'ms')
    console.warn('\n[C# Parser] 输出:')
    console.warn(run.output)

    // 读取生成的文件
    const outputPath = path.join(exeDir, 'appinfo_text.vdf')
    if (fs.existsSync(outputPath)) {
      const stats = fs.statSync(outputPath)
      console.warn(`\n[C# Parser] 生成文件: ${outputPath}`)
      console.warn(`[C# Parser] 文件大小: ${(stats.size / 1024 / 1024).toFixed(2)} MB`)

      // 读取前 2000 字符预览
      const content = fs.readFileSync(outputPath, 'utf8')
      console.warn('\n[C# Parser] 文件预览 (前 2000 字符):')
      console.warn('─'.repeat(80))
      console.warn(content.substring(0, 2000))
      if (content.length > 2000) {
        console.warn('...')
      }
      console.warn('─'.repeat(80))
    }

    // 步骤 6: 对比我们的 TS 实现
    console.warn('\n[C# Parser] ==================== 对比 TypeScript 实现 ====================')
    console.warn('[TS Parser] 开始解析...')
    const tsStartTime = Date.now()
    const tsApps = parseAppInfo(appinfoPath)
    const tsDuration = Date.now() - tsStartTime

    console.warn('[TS Parser] ✅ 解析完成')
    console.warn('[TS Parser] 耗时:', tsDuration, 'ms')
    console.warn('[TS Parser] 应用数量:', tsApps.length)

    // 提取 C# 输出中的应用数量
    const csharpAppsMatch = run.output.match(/(\d+)\s+apps/)
    const csharpAppsCount = csharpAppsMatch ? Number.parseInt(csharpAppsMatch[1]) : 0

    console.warn('\n[对比结果] ==================== 性能和准确性对比 ====================')
    console.warn(`应用数量:`)
    console.warn(`  C# :  ${csharpAppsCount} 个`)
    console.warn(`  TS :  ${tsApps.length} 个`)
    console.warn(`  匹配: ${csharpAppsCount === tsApps.length ? '✅' : '❌'}`)

    console.warn(`\n解析速度:`)
    console.warn(`  C# :  ${duration} ms`)
    console.warn(`  TS :  ${tsDuration} ms`)
    console.warn(`  倍数: ${(tsDuration / duration).toFixed(2)}x ${duration < tsDuration ? '(C# 更快)' : '(TS 更快)'}`)

    // 显示几个示例应用对比
    console.warn('\n[对比结果] 示例应用对比:')
    const testAppIds = [730, 440, 570, 271590]
    for (const appid of testAppIds) {
      const tsApp = tsApps.find(app => app.appid === appid)
      if (tsApp) {
        console.warn(`\n  AppID ${appid}:`)
        console.warn(`    名称: ${tsApp.data?.name || '(未找到)'}`)
        console.warn(`    类型: ${tsApp.data?.type || '(未找到)'}`)
        console.warn(`    操作系统: ${tsApp.data?.oslist || '(未找到)'}`)
      }
    }

    console.warn('\n[C# Parser] ==================== 完成 ====================')
    console.warn('[提示] C# 生成的完整文本文件位于:')
    console.warn(`  ${outputPath}`)
    console.warn('[提示] 可以用文本编辑器打开查看完整的 VDF 数据结构')
  }
  catch (error) {
    console.error('[C# Parser] 运行失败:', error)
  }
}

/**
 * 运行命令行程序
 */
function runCommand(command: string, args: string[], cwd: string): Promise<{ success: boolean, output: string, error: string }> {
  return new Promise((resolve) => {
    let output = ''
    let error = ''

    const process = spawn(command, args, {
      cwd,
      shell: true,
    })

    process.stdout?.on('data', (data) => {
      const text = data.toString()
      output += text
    })

    process.stderr?.on('data', (data) => {
      const text = data.toString()
      error += text
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
