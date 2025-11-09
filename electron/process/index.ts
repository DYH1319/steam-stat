import si from 'systeminformation'

export interface ProcessInfo {
  name: string
  pid: number
  command: string
  path: string
  params: string
  cpu: number
  mem: number
  memVsz: number
  memRss: number
  user: string
}

/**
 * 使用 systeminformation 获取进程列表
 * @returns 进程信息列表
 */
export async function getProcessList(): Promise<ProcessInfo[]> {
  try {
    const processes = await si.processes()
    const processList: ProcessInfo[] = processes.list.map((proc: any) => ({
      name: proc.name || '',
      pid: proc.pid || 0,
      command: proc.command || '',
      path: proc.path || '',
      params: proc.params || '',
      cpu: proc.cpu || 0,
      mem: proc.mem || 0,
      memVsz: proc.memVsz || 0,
      memRss: proc.memRss || 0,
      user: proc.user || '',
    }))

    console.warn('[进程监控] 进程数量:', processList.length)
    return processList
  }
  catch (error) {
    console.error('[进程监控] 获取进程列表失败:', error)
    return []
  }
}
