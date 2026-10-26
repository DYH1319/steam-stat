import type { ClassValue } from 'clsx'
import { clsx } from 'clsx'
import path from 'path-browserify'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

export function resolveRoutePath(basePath?: string, routePath?: string) {
  return basePath ? path.resolve(basePath, routePath ?? '') : routePath ?? ''
}

/**
 * 将本地绝对路径转换为 steam-stat-file:// 协议 URL
 * @param path 本地文件绝对路径
 */
export function encodeFileUrl(path: string | null | undefined): string {
  if (!path) {
    return ''
  }

  // 将 Windows 路径分隔符转换为正斜杠，并进行 URL 编码
  const normalizedPath = path.replace(/\\/g, '/')
  const encodedPath = encodeURIComponent(normalizedPath)
  return `steam-stat-file://${encodedPath}`
}

/**
 * 格式化字节大小
 * @param bytes 字节大小（2^53-1以内）
 */
export function formatBytes(bytes: bigint | number | null | undefined): string {
  if (!bytes) {
    return '0 B'
  }
  const value = typeof bytes === 'bigint' ? Number(bytes) : bytes
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const k = 1024
  const i = Math.floor(Math.log(value) / Math.log(k))
  return `${(value / k ** i).toFixed(2)} ${units[i]}`
}
