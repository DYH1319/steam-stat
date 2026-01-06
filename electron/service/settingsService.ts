import type { AppSettings } from '../types/settings'
import fs from 'node:fs'
import path from 'node:path'
import { app } from 'electron'

const DEFAULT_SETTINGS: AppSettings = {
  autoStart: false,
  silentStart: false,
  autoUpdate: true,
  language: app.getLocale(),
  closeAction: 'ask',
  updateAppRunningStatusJob: {
    enabled: true,
    intervalSeconds: 5,
  },
}

let settingsFilePath: string

/**
 * 获取设置文件路径
 */
function getSettingsFilePath(): string {
  if (!settingsFilePath) {
    const userDataPath = app.getPath('userData')
    const settingsDir = path.join(userDataPath, 'Settings')

    // 确保设置目录存在
    if (!fs.existsSync(settingsDir)) {
      fs.mkdirSync(settingsDir, { recursive: true })
    }

    settingsFilePath = path.join(settingsDir, 'app-settings.json')
  }
  return settingsFilePath
}

/**
 * 读取设置
 */
export function getSettings(): AppSettings {
  try {
    const filePath = getSettingsFilePath()
    if (fs.existsSync(filePath)) {
      const data = fs.readFileSync(filePath, 'utf-8')
      const settings = JSON.parse(data) as AppSettings
      // 合并默认设置，确保新增的设置项也有默认值
      return { ...DEFAULT_SETTINGS, ...settings }
    }
  }
  catch (error) {
    console.error('[Settings] 读取设置失败:', error)
  }
  return { ...DEFAULT_SETTINGS }
}

/**
 * 保存设置
 */
export function saveSettings(settings: Partial<AppSettings>): boolean {
  try {
    const filePath = getSettingsFilePath()
    const currentSettings = getSettings()
    const newSettings = { ...currentSettings, ...settings }
    fs.writeFileSync(filePath, JSON.stringify(newSettings, null, 2), 'utf-8')
    console.warn('[Settings] 设置已保存:', newSettings)
    return true
  }
  catch (error) {
    console.error('[Settings] 保存设置失败:', error)
    return false
  }
}

/**
 * 更新部分设置
 */
export function updateSettings(partialSettings: Partial<AppSettings>): boolean {
  return saveSettings(partialSettings)
}

/**
 * 重置设置为默认值
 */
export function resetSettings(): boolean {
  return saveSettings(DEFAULT_SETTINGS)
}
