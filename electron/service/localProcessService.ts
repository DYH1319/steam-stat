/**
 * 本地进程服务
 */
import { exec, spawn } from 'node:child_process'
import process from 'node:process'
import si from 'systeminformation'

/**
 * 进程信息接口
 */
export interface ProcessInfo {
  pid: number
  name: string
  command: string
  cpu?: number
  mem?: number
}

/**
 * 根据进程名称获取进程列表（一个名称可能对应多个进程）
 * @param processName 进程名称（Windows 下可以不加 .exe 后缀）
 * @returns 进程信息列表
 */
export async function getProcessesByName(processName: string): Promise<ProcessInfo[]> {
  try {
    const processes = await si.processes()
    const processNameLower = processName.toLowerCase()
    const processNameWithExt = processNameLower.endsWith('.exe') ? processNameLower : `${processNameLower}.exe`

    return processes.list
      .filter((proc) => {
        const name = proc.name.toLowerCase()
        // 匹配进程名（Windows 下支持带或不带 .exe 后缀）
        return name === processNameLower || name === processNameWithExt
      })
      .map(proc => ({
        pid: proc.pid,
        name: proc.name,
        command: proc.command,
        cpu: proc.cpu,
        mem: proc.mem,
      }))
  }
  catch (error) {
    console.error('[LocalProcess] 获取进程失败:', error)
    return []
  }
}

/**
 * 强制杀死进程
 * @param pidOrProcess 进程 PID 或进程信息对象
 * @returns 是否成功
 */
export async function killProcess(pidOrProcess: number | ProcessInfo): Promise<boolean> {
  const pid = typeof pidOrProcess === 'number' ? pidOrProcess : pidOrProcess.pid

  return new Promise((resolve) => {
    try {
      if (process.platform === 'win32') {
        // Windows: 使用 taskkill /F /PID
        exec(`taskkill /F /PID ${pid}`, (error) => {
          if (error) {
            console.error(`[LocalProcess] 强制杀死进程 ${pid} 失败:`, error)
            resolve(false)
          }
          else {
            console.warn(`[LocalProcess] 成功强制杀死进程 ${pid}`)
            resolve(true)
          }
        })
      }
      else {
        // macOS/Linux: 使用 kill -9
        exec(`kill -9 ${pid}`, (error) => {
          if (error) {
            console.error(`[LocalProcess] 强制杀死进程 ${pid} 失败:`, error)
            resolve(false)
          }
          else {
            console.warn(`[LocalProcess] 成功强制杀死进程 ${pid}`)
            resolve(true)
          }
        })
      }
    }
    catch (error) {
      console.error(`[LocalProcess] 强制杀死进程 ${pid} 异常:`, error)
      resolve(false)
    }
  })
}

/**
 * 正常停止进程（尝试优雅退出）
 * @param pidOrProcess 进程 PID 或进程信息对象
 * @returns 是否成功
 */
export async function stopProcess(pidOrProcess: number | ProcessInfo): Promise<boolean> {
  const pid = typeof pidOrProcess === 'number' ? pidOrProcess : pidOrProcess.pid

  return new Promise((resolve) => {
    try {
      if (process.platform === 'win32') {
        // Windows: 使用 taskkill（不带 /F 参数，允许进程正常退出）
        exec(`taskkill /PID ${pid}`, (error) => {
          if (error) {
            console.error(`[LocalProcess] 正常停止进程 ${pid} 失败:`, error)
            resolve(false)
          }
          else {
            console.warn(`[LocalProcess] 成功正常停止进程 ${pid}`)
            resolve(true)
          }
        })
      }
      else {
        // macOS/Linux: 使用 kill（默认 SIGTERM 信号）
        exec(`kill ${pid}`, (error) => {
          if (error) {
            console.error(`[LocalProcess] 正常停止进程 ${pid} 失败:`, error)
            resolve(false)
          }
          else {
            console.warn(`[LocalProcess] 成功正常停止进程 ${pid}`)
            resolve(true)
          }
        })
      }
    }
    catch (error) {
      console.error(`[LocalProcess] 正常停止进程 ${pid} 异常:`, error)
      resolve(false)
    }
  })
}

/**
 * 根据可执行文件路径启动程序
 * @param executablePath 可执行文件完整路径
 * @param args 命令行参数（可选）
 * @param options 启动选项（可选）
 * @param options.cwd 工作目录
 * @param options.detached 是否分离进程
 * @returns 子进程对象，如果启动失败返回 null
 */
export function startProcess(
  executablePath: string,
  args: string[] = [],
  options: { cwd?: string, detached?: boolean } = {},
) {
  try {
    const childProcess = spawn(executablePath, args, {
      detached: options.detached ?? true, // 默认分离进程
      stdio: 'ignore', // 忽略输入输出
      cwd: options.cwd,
    })

    // 如果是分离进程，需要 unref 以允许父进程退出
    if (options.detached ?? true) {
      childProcess.unref()
    }

    childProcess.on('error', (error) => {
      console.error(`[LocalProcess] 启动进程失败 [${executablePath}]:`, error)
    })

    console.warn(`[LocalProcess] 成功启动进程: ${executablePath}, PID: ${childProcess.pid}`)
    return childProcess
  }
  catch (error) {
    console.error(`[LocalProcess] 启动进程异常 [${executablePath}]:`, error)
    return null
  }
}

// Test
// (async () => {
//   // 获取 steam 所有进程
//   const processes: ProcessInfo[] = []
//   for (const processName of STEAM_PROCESSES) {
//     console.warn(`[LocalProcess] 获取进程 ${processName}`)
//     const _processes = await getProcessesByName(processName)
//     console.warn(`[LocalProcess] 获取进程 ${processName} 结果:`, _processes)
//     processes.push(..._processes)
//   }
//   console.warn(`[LocalProcess] 获取 steam 所有进程个数: ${processes.length}`)
//   // 多线程杀死所有 steam 进程
//   const killProcessPromises = processes.map((process) => {
//     console.warn(`[LocalProcess] 杀死进程: ${process.name} PID: ${process.pid}`)
//     return killProcess(process.pid)
//   })
//   await Promise.all(killProcessPromises)
//   console.warn(`[LocalProcess] 杀死 steam 所有进程个数: ${processes.length}`)
//   // 重新启动 steam
//   const steamPath = 'D:\\Program Files (x86)\\Steam\\steam.exe'
//   const steamProcess = startProcess(steamPath)
//   console.warn(`[LocalProcess] 重新启动 steam, PID: ${steamProcess?.pid}`)
//   // 等待 10 秒
//   await new Promise(resolve => setTimeout(resolve, 10000))
// })()
