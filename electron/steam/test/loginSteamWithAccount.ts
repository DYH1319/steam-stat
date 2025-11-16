import type { BrowserWindow } from 'electron'
import type { EAuthSessionGuardType } from 'steam-session'
import { EAuthTokenPlatformType, EResult, LoginSession } from 'steam-session'

export interface LoginAccountParams {
  accountName: string
  password: string
  steamGuardMachineToken?: string
}

export interface LoginAccountResult {
  success: boolean
  needAction?: boolean
  validActions?: Array<{ type: EAuthSessionGuardType, detail?: string }>
  error?: string
  sessionId?: string
}

export interface SubmitCodeParams {
  sessionId: string
  code: string
}

export interface AuthenticatedResult {
  steamID: string
  accountName: string
  accessToken: string
  refreshToken: string
  steamGuardMachineToken?: string
  webCookies?: string[]
}

// 存储活动的登录会话
const activeSessions = new Map<string, LoginSession>()

/**
 * 开始账号密码登录
 */
export async function startLoginWithAccount(
  params: LoginAccountParams,
  win: BrowserWindow,
): Promise<LoginAccountResult> {
  try {
    const session = new LoginSession(EAuthTokenPlatformType.SteamClient)
    const sessionId = `account_${Date.now()}_${Math.random().toString(36).substring(7)}`

    activeSessions.set(sessionId, session)

    // 设置超时时间
    session.loginTimeout = 120000 // 2分钟

    session.on('debug', console.warn)
    session.on('debug-handler', console.error)

    // 监听事件
    session.on('steamGuardMachineToken', () => {
      win.webContents.send('steam-login-event', {
        type: 'machineToken',
        sessionId,
        data: {
          steamGuardMachineToken: session.steamGuardMachineToken,
        },
      })
    })

    session.on('authenticated', async () => {
      const webCookies = await session.getWebCookies()
      const result: AuthenticatedResult = {
        steamID: session.steamID?.toString() || '',
        accountName: session.accountName || '',
        accessToken: session.accessToken || '',
        refreshToken: session.refreshToken || '',
        steamGuardMachineToken: session.steamGuardMachineToken,
        webCookies,
      }

      win.webContents.send('steam-login-event', {
        type: 'authenticated',
        sessionId,
        data: result,
      })

      // 清理会话
      activeSessions.delete(sessionId)
    })

    session.on('timeout', () => {
      win.webContents.send('steam-login-event', {
        type: 'timeout',
        sessionId,
      })
      activeSessions.delete(sessionId)
    })

    session.on('error', (err) => {
      win.webContents.send('steam-login-event', {
        type: 'error',
        sessionId,
        data: { error: err.message },
      })
      activeSessions.delete(sessionId)
    })

    // 开始登录
    const startResult = await session.startWithCredentials({
      accountName: params.accountName,
      password: params.password,
      steamGuardMachineToken: params.steamGuardMachineToken,
    })

    if (startResult.actionRequired) {
      return {
        success: true,
        needAction: true,
        validActions: startResult.validActions?.map(action => ({
          type: action.type,
          detail: action.detail,
        })),
        sessionId,
      }
    }

    return {
      success: true,
      needAction: false,
      sessionId,
    }
  }
  catch (error: any) {
    return {
      success: false,
      error: error.message || '登录失败',
    }
  }
}

/**
 * 提交验证码
 */
export async function submitSteamGuardCode(
  params: SubmitCodeParams,
): Promise<{ success: boolean, error?: string }> {
  try {
    const session = activeSessions.get(params.sessionId)
    if (!session) {
      return {
        success: false,
        error: '会话不存在或已过期',
      }
    }

    await session.submitSteamGuardCode(params.code)
    return { success: true }
  }
  catch (error: any) {
    if (error.eresult === EResult.TwoFactorCodeMismatch) {
      return {
        success: false,
        error: '验证码错误',
      }
    }
    return {
      success: false,
      error: error.message || '提交验证码失败',
    }
  }
}

/**
 * 取消登录会话
 */
export function cancelLoginSession(sessionId: string): void {
  const session = activeSessions.get(sessionId)
  if (session) {
    // LoginSession 没有显式的 cancel 方法,但我们可以删除引用
    activeSessions.delete(sessionId)
  }
}
