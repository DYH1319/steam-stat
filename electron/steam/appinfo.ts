/**
 * appinfo.vdf 解析模块
 * 基于 SteamDB 的 SteamAppInfoParser 实现
 * 支持 appinfo.vdf v39-v41 格式
 *
 * 简化版本：只解析基本元数据，VDF 数据部分使用 @node-steam/vdf
 */

import { Buffer } from 'node:buffer'
import fs from 'node:fs'

/**
 * Universe 枚举
 */
export enum EUniverse {
  Invalid = 0,
  Public = 1,
  Beta = 2,
  Internal = 3,
  Dev = 4,
  Max = 5,
}

/**
 * App 信息
 */
export interface AppInfo {
  appid: number
  infoState: number
  lastUpdated: Date
  token: bigint
  hash: Buffer // 20 字节 SHA1
  binaryDataHash?: Buffer // 20 字节 SHA1 (v40+)
  changeNumber: number
  data: any // VDF 数据 (原始 Buffer，暂不解析)
}

/**
 * Unix 时间戳转 Date
 */
function dateTimeFromUnixTime(unixTime: number): Date {
  return new Date(unixTime * 1000)
}

/**
 * 解析 appinfo.vdf 文件
 * @param filePath 文件路径
 * @returns App 信息数组
 */
export function parseAppInfo(filePath: string): AppInfo[] {
  const buf = fs.readFileSync(filePath)
  const apps: AppInfo[] = []

  let pos = 0

  // 读取 magic 和 version
  const magicAndVersion = buf.readUInt32LE(0)
  pos += 4

  const version = magicAndVersion & 0xFF // 最低 8 位是版本号
  const magic = magicAndVersion >> 8 // 高 24 位是 magic

  console.warn(`[AppInfo] Magic: 0x${magic.toString(16).toUpperCase().padStart(6, '0')}`)
  console.warn(`[AppInfo] Version: ${version}`)

  if (magic !== 0x07_56_44) {
    throw new Error(`Unknown magic header: 0x${magic.toString(16)}`)
  }

  if (version < 39 || version > 41) {
    throw new Error(`Unsupported version: ${version} (only v39-v41 supported)`)
  }

  // 读取 Universe
  const universe = buf.readUInt32LE(pos) as EUniverse
  pos += 4
  console.warn(`[AppInfo] Universe: ${EUniverse[universe]} (${universe})`)

  // v41+ 有 string table，必须读取它才能解析数据
  const stringTable: string[] = []
  if (version >= 41) {
    // 读取 string table offset (8 字节 BigInt)
    const low = buf.readUInt32LE(pos)
    const high = buf.readUInt32LE(pos + 4)
    // 将两个 32 位整数组合成一个数字（假设不会超过 Number.MAX_SAFE_INTEGER）
    const stringTableOffset = low + high * 0x100000000
    pos += 8
    console.warn(`[AppInfo] String table offset: ${stringTableOffset}`)

    // 读取 string table
    const savedPos = pos // 保存当前位置

    // 跳转到 string table
    let strPos = stringTableOffset
    const stringCount = buf.readUInt32LE(strPos)
    strPos += 4
    console.warn(`[AppInfo] String table count: ${stringCount}`)

    // 读取所有字符串
    for (let i = 0; i < stringCount; i++) {
      let end = strPos
      while (end < buf.length && buf[end] !== 0) {
        end++
      }
      const str = buf.toString('utf8', strPos, end)
      stringTable.push(str)
      strPos = end + 1 // 跳过 null 终止符
    }

    console.warn(`[AppInfo] String table 读取完成，共 ${stringTable.length} 个字符串`)

    // 恢复位置
    pos = savedPos
  }

  // 解析所有 App 条目
  let appCount = 0
  while (pos < buf.length - 4) {
    const appid = buf.readUInt32LE(pos)
    pos += 4

    if (appid === 0) {
      console.warn('[AppInfo] 遇到终止标记 (AppID = 0)')
      break
    }

    const size = buf.readUInt32LE(pos) // size 到 Data 结束
    pos += 4
    const endPos = pos + size

    // 检查是否超出缓冲区
    if (endPos > buf.length) {
      console.warn(`[AppInfo] AppID ${appid}: 数据超出文件范围，停止解析`)
      break
    }

    const infoState = buf.readUInt32LE(pos)
    pos += 4

    const lastUpdatedUnix = buf.readUInt32LE(pos)
    pos += 4
    const lastUpdated = dateTimeFromUnixTime(lastUpdatedUnix)

    const token = buf.readBigUInt64LE(pos)
    pos += 8

    const hash = Buffer.from(buf.subarray(pos, pos + 20))
    pos += 20

    const changeNumber = buf.readUInt32LE(pos)
    pos += 4

    let binaryDataHash: Buffer | undefined
    if (version >= 40) {
      binaryDataHash = Buffer.from(buf.subarray(pos, pos + 20))
      pos += 20
    }

    // VDF 数据部分：暂时保存为原始 Buffer
    // 因为解析 Binary VDF 比较复杂，我们暂时只提取关键元数据
    const vdfData = buf.subarray(pos, endPos)

    // 尝试提取应用名称
    const data = extractBasicInfo(vdfData, appid, stringTable, version)

    pos = endPos

    apps.push({
      appid,
      infoState,
      lastUpdated,
      token,
      hash,
      binaryDataHash,
      changeNumber,
      data,
    })

    appCount++
    if (appCount % 500 === 0) {
      console.warn(`[AppInfo] 已解析 ${appCount} 个应用... (位置: ${pos}/${buf.length})`)
    }

    // 防止无限循环
    if (appCount >= 100000) {
      console.warn('[AppInfo] 达到最大应用数限制 (100000)')
      break
    }
  }

  console.warn(`[AppInfo] 解析完成，共 ${apps.length} 个应用`)
  return apps
}

/**
 * 从 VDF Buffer 中提取基本信息（应用名称等）
 * v41 使用 String Table，所有字符串都是索引
 */
function extractBasicInfo(vdfBuf: Buffer, _appid: number, stringTable: string[], version: number): any {
  const result: any = {}

  try {
    if (version >= 41 && stringTable.length > 0) {
      // v41 格式：使用 String Table
      const parseResult = parseVDFWithStringTable(vdfBuf, 0, vdfBuf.length, stringTable, 0, _appid)
      result.raw = parseResult.data

      // 提取常用字段
      if (result.raw?.appinfo?.common) {
        const common = result.raw.appinfo.common
        if (common.name) {
          result.name = common.name
        }
        if (common.type) {
          result.type = common.type
        }
        if (common.oslist) {
          result.oslist = common.oslist
        }
        if (common.developer) {
          result.developer = common.developer
        }
        if (common.publisher) {
          result.publisher = common.publisher
        }
      }
    }
    else {
      // 旧版本：搜索字符串
      const searchKeys = ['name', 'type', 'oslist', 'developer', 'publisher']
      for (const key of searchKeys) {
        const value = searchKeyValue(vdfBuf, key)
        if (value) {
          result[key] = value
        }
      }
    }
  }
  catch (error) {
    // 忽略解析错误，但记录日志
    if (_appid === 5) {
      console.warn('[AppInfo] 解析 VDF 失败:', error)
    }
  }

  return result
}

/**
 * 解析使用 String Table 的 Binary VDF 数据
 * v41 格式：[type][stringIndex][value...]
 * 注意：字符串值可以是 String Table 索引或内联的 null 结尾字符串
 */
function parseVDFWithStringTable(buf: Buffer, startPos: number, endPos: number, stringTable: string[], depth = 0, _debugAppId?: number): { data: any, next: number } {
  let pos = startPos
  const result: any = {}

  while (pos < endPos) {
    if (pos >= buf.length) {
      break
    }

    const type = buf[pos]
    pos++

    // 0x08 = 结束标记
    if (type === 0x08) {
      break
    }

    // 读取键名索引（4 字节整数）
    if (pos + 4 > buf.length) {
      break
    }
    const keyIndex = buf.readUInt32LE(pos)
    pos += 4

    const key = stringTable[keyIndex] || `<${keyIndex}>`

    let value: any

    try {
      switch (type) {
        case 0x00: // 嵌套对象
          {
            // 递归解析嵌套对象
            const nested = parseVDFWithStringTable(buf, pos, endPos, stringTable, depth + 1, _debugAppId)
            value = nested.data
            pos = nested.next
          }
          break
        case 0x01: // 字符串
          {
            if (pos + 4 > buf.length) {
              break
            }

            // 尝试读取索引
            const possibleIndex = buf.readUInt32LE(pos)

            // 如果索引在有效范围内，使用 String Table
            if (possibleIndex < stringTable.length) {
              pos += 4
              value = stringTable[possibleIndex] || ''
            }
            else {
              // 否则，可能是内联的 null 结尾字符串
              let end = pos
              while (end < endPos && buf[end] !== 0) {
                end++
              }
              value = buf.toString('utf8', pos, end)
              pos = end + 1 // 跳过 null
            }
          }
          break
        case 0x02: // Int32
          if (pos + 4 > buf.length) {
            break
          }
          value = buf.readInt32LE(pos)
          pos += 4
          break
        case 0x07: // UInt64
          if (pos + 8 > buf.length) {
            break
          }
          value = buf.readBigUInt64LE(pos).toString()
          pos += 8
          break
        default:
          // 未知类型，跳过
          break
      }

      if (value !== undefined) {
        result[key] = value
      }
    }
    catch {
      // 忽略错误并跳出循环
      break
    }
  }

  return { data: result, next: pos }
}

/**
 * 在 VDF Buffer 中搜索键值对
 * Binary VDF 格式：[type byte][key\0][value...]
 * type=0x01 表示字符串，value 格式为 [string\0]
 */
function searchKeyValue(buf: Buffer, searchKey: string): string | null {
  try {
    // 在 Binary VDF 中，格式是：
    // 0x00 [key\0] ... (嵌套对象)
    // 0x01 [key\0] [value\0] (字符串)
    // 0x02 [key\0] [int32] (整数)
    // etc.

    // 搜索模式：0x01 [searchKey\0]
    const searchPattern = Buffer.concat([
      Buffer.from([0x01]), // 类型：字符串
      Buffer.from(searchKey, 'utf8'),
      Buffer.from([0x00]), // null 终止符
    ])

    for (let i = 0; i <= buf.length - searchPattern.length; i++) {
      // 检查是否匹配
      let match = true
      for (let j = 0; j < searchPattern.length; j++) {
        if (buf[i + j] !== searchPattern[j]) {
          match = false
          break
        }
      }

      if (match) {
        // 找到键，读取值（字符串，null 结尾）
        const pos = i + searchPattern.length
        let end = pos

        // 读取直到遇到 null
        while (end < buf.length && buf[end] !== 0 && (end - pos) < 1000) {
          end++
        }

        if (end > pos) {
          const value = buf.toString('utf8', pos, end)
          // 验证是否是可打印的字符串
          if (value && value.length > 0 && value.length < 500) {
            // 简单验证：至少包含一些可打印字符
            const printable = /[\x20-\x7E]/.test(value)
            if (printable) {
              return value.trim()
            }
          }
        }

        // 如果第一次找到没有有效值，继续搜索
      }
    }
  }
  catch {
    // 忽略错误
  }

  return null
}

/**
 * 根据 AppID 获取应用信息
 */
export function getAppInfoById(filePath: string, appid: number): AppInfo | null {
  const apps = parseAppInfo(filePath)
  return apps.find(app => app.appid === appid) || null
}

/**
 * 获取已安装应用的信息（通过 AppID 列表）
 */
export function getInstalledAppsInfo(filePath: string, appids: number[]): AppInfo[] {
  const apps = parseAppInfo(filePath)
  const appidSet = new Set(appids)
  return apps.filter(app => appidSet.has(app.appid))
}
