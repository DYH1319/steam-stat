import type { BrowserWindow } from 'electron'
import type { AuthenticatedResult } from './loginSteamWithAccount'
import { EAuthTokenPlatformType, LoginSession } from 'steam-session'

export interface LoginQRCodeResult {
  success: boolean
  qrChallengeUrl?: string
  qrCodeImageUrl?: string
  sessionId?: string
  error?: string
}

// 存储活动的二维码登录会话
const activeQRSessions = new Map<string, LoginSession>()

/**
 * 开始二维码登录
 */
export async function startLoginWithQRCode(
  win: BrowserWindow,
  httpProxy?: string,
): Promise<LoginQRCodeResult> {
  try {
    const sessionOptions = httpProxy ? { httpProxy } : undefined
    const session = new LoginSession(EAuthTokenPlatformType.MobileApp, sessionOptions)
    const sessionId = `qr_${Date.now()}_${Math.random().toString(36).substring(7)}`

    activeQRSessions.set(sessionId, session)

    // 设置超时时间
    session.loginTimeout = 120000 // 2分钟

    // 监听扫码事件
    session.on('remoteInteraction', () => {
      win.webContents.send('steam-qr-login-event', {
        type: 'remoteInteraction',
        sessionId,
        data: { message: '二维码已被扫描,请在手机上确认登录' },
      })
    })

    // 监听认证成功事件
    session.on('authenticated', async () => {
      const webCookies = await session.getWebCookies()
      const result: AuthenticatedResult = {
        steamID: session.steamID?.toString() || '',
        accountName: session.accountName || '',
        accessToken: session.accessToken || '',
        refreshToken: session.refreshToken || '',
        webCookies,
      }

      win.webContents.send('steam-qr-login-event', {
        type: 'authenticated',
        sessionId,
        data: result,
      })

      // 清理会话
      activeQRSessions.delete(sessionId)
    })

    // 监听超时事件
    session.on('timeout', () => {
      win.webContents.send('steam-qr-login-event', {
        type: 'timeout',
        sessionId,
      })
      activeQRSessions.delete(sessionId)
    })

    // 监听错误事件
    session.on('error', (err) => {
      win.webContents.send('steam-qr-login-event', {
        type: 'error',
        sessionId,
        data: { error: err.message },
      })
      activeQRSessions.delete(sessionId)
    })

    // 开始二维码登录
    const startResult = await session.startWithQR()

    // 生成二维码图片URL
    const qrCodeImageUrl = `https://api.qrserver.com/v1/create-qr-code/?size=300x300&data=${encodeURIComponent(startResult.qrChallengeUrl ?? '')}`

    return {
      success: true,
      qrChallengeUrl: startResult.qrChallengeUrl,
      qrCodeImageUrl,
      sessionId,
    }
  }
  catch (error: any) {
    return {
      success: false,
      error: error.message || '启动二维码登录失败',
    }
  }
}

/**
 * 取消二维码登录会话
 */
export function cancelQRLoginSession(sessionId: string): void {
  const session = activeQRSessions.get(sessionId)
  if (session) {
    activeQRSessions.delete(sessionId)
  }
}
