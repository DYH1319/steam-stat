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
 * @param steamIds 可选，筛选特定用户的记录
 * @param startDate 可选，开始日期（时间戳）
 * @param endDate 可选，结束日期（时间戳）
 */
export async function getValidUseAppRecord(
  steamIds?: bigint[],
  startDate?: number,
  endDate?: number,
): Promise<{ records: GetValidRecordsResponse[], lastUpdateTime: number }> {
  const jobStatus = updateAppRunningStatusJob.getJobStatus()

  // 转换参数类型
  const startDateObj = startDate ? new Date(startDate) : undefined
  const endDateObj = endDate ? new Date(endDate) : undefined

  const records = await useAppRecord.getValidRecords(steamIds, startDateObj, endDateObj)

  return {
    records,
    lastUpdateTime: jobStatus.lastUpdateTime,
  }
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
