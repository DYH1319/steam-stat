import type { UseAppRecord } from '../db/schema'
import type { GetValidRecordsResponse } from '../types/useAppRecord'
import * as useAppRecord from '../db/useAppRecord'
import * as updateAppRunningStatusJob from '../job/updateAppRunningStatusJob'

/**
 * 初始化或更新 UseAppRecord 到数据库
 */
export async function initOrUpdateUseAppRecord() {
  await useAppRecord.initUseAppRecord()
}

/**
 * 获取 UseAppRecord
 */
export async function getUseAppRecord(): Promise<UseAppRecord[]> {
  return useAppRecord.getAllRecords()
}

/**
 * 获取有效的 UseAppRecord
 */
export async function getValidUseAppRecord(): Promise<{ records: GetValidRecordsResponse[], lastUpdateTime: number }> {
  const jobStatus = updateAppRunningStatusJob.getJobStatus()
  return { records: await useAppRecord.getValidRecords(), lastUpdateTime: jobStatus.lastUpdateTime }
}

/**
 * 开始记录
 */
export async function startRecord(steamId: bigint, appId: number) {
  await useAppRecord.startRecord(steamId, appId)
}

/**
 * 结束记录
 */
export async function stopRecord(steamId: bigint, appId: number) {
  await useAppRecord.stopRecord(steamId, appId)
}
