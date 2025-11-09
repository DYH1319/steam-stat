// binaryVdf.ts
import type { Buffer } from 'node:buffer'
import * as fs from 'node:fs'

const TYPE_OBJECT = 0x00
const TYPE_STRING = 0x01
const TYPE_INT32 = 0x02
const TYPE_UINT64 = 0x07
const TYPE_END = 0x08

function bigIntToString(obj: any): any {
  if (typeof obj === 'bigint') {
    return obj.toString() // 安全输出
  }
  if (Array.isArray(obj)) {
    return obj.map(bigIntToString)
  }
  if (obj && typeof obj === 'object') {
    const result: any = {}
    for (const key in obj) {
      result[key] = bigIntToString(obj[key])
    }
    return result
  }
  return obj
}

function readCString(buf: Buffer, pos: number) {
  const start = pos
  while (pos < buf.length && buf[pos] !== 0) {
    pos++
  }
  return {
    str: buf.toString('utf8', start, pos),
    next: pos + 1,
  }
}

/**
 * 解析二进制 VDF 数据
 */
function parseObject(buf: Buffer, pos: number): { obj: any, next: number } {
  const obj: any = {}

  while (true) {
    const t = buf[pos]
    pos++

    if (t === TYPE_END) {
      break
    }

    const keyInfo = readCString(buf, pos)
    const key = keyInfo.str
    pos = keyInfo.next

    let value: any

    if (t === TYPE_OBJECT) {
      const res = parseObject(buf, pos)
      value = res.obj
      pos = res.next
    }
    else if (t === TYPE_STRING) {
      const v = readCString(buf, pos)
      value = v.str
      pos = v.next
    }
    else if (t === TYPE_INT32) {
      value = buf.readInt32LE(pos)
      pos += 4
    }
    else if (t === TYPE_UINT64) {
      // read uint64 safely as BigInt
      value = buf.readBigUInt64LE(pos)
      pos += 8
    }
    else {
      throw new Error(`Unknown VDF type: ${t}`)
    }

    obj[key] = value
  }

  return { obj, next: pos }
}

export function readBinaryVdf(path: string) {
  const buf = fs.readFileSync(path)
  const { obj } = parseObject(buf, 0)
  return bigIntToString(obj)
}
