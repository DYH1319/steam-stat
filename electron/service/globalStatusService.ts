import type { GlobalStatus } from '../db/schema'
import * as globalStatus from '../db/globalStatus'
import * as localRegService from './localRegService'

/**
 * 初始化或更新全局状态信息到数据库
 */
export async function initOrUpdateGlobalStatus() {
  const steamReg = await localRegService.readSteamReg()
  const steamActiveProcessReg = await localRegService.readSteamActiveProcessReg()
  await globalStatus.insertOrUpdateGlobalStatus(steamReg, steamActiveProcessReg)
}

/**
 * 获取全局状态信息
 */
export async function getGlobalStatus(): Promise<GlobalStatus> {
  return globalStatus.getGlobalStatus()
}

/**
 * 刷新全局状态信息（重新读取注册表并写入数据库，返回最新数据）
 */
export async function refreshGlobalStatus(): Promise<GlobalStatus> {
  await initOrUpdateGlobalStatus()
  return globalStatus.getGlobalStatus()
}

/**
 * 更新 Steam 用户刷新时间
 */
export async function updateSteamUserRefreshTime() {
  await globalStatus.updateSteamUserRefreshTime()
}
