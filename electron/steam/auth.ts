/**
 * Steam 认证模块
 * 使用 steam-user 库登录 Steam 并获取 Store Access Token
 */

import { createRequire } from 'node:module'
import axios from 'axios'

// 使用 createRequire 加载 CommonJS 模块
const require = createRequire(import.meta.url)
const SteamUser = require('steam-user')

export interface SteamLoginOptions {
  accountName: string
  password: string
  twoFactorCode?: string // Steam Guard 移动验证器代码
  authCode?: string // Steam Guard 邮箱验证码
  rememberPassword?: boolean
}

export interface SteamStoreAccessToken {
  success: boolean
  token?: string
  error?: string
}

export interface SteamWebSession {
  sessionID: string
  cookies: string[]
  steamID: string
}

class SteamAuthManager {
  private client: any = null
  private webSession: SteamWebSession | null = null
  private isLoggedIn: boolean = false

  constructor() {
    this.client = new SteamUser()
    this.setupEventHandlers()
  }

  /**
   * 设置事件处理器
   */
  private setupEventHandlers() {
    if (!this.client) {
      return
    }

    // 登录成功
    this.client.on('loggedOn', () => {
      this.isLoggedIn = true
    })

    // 获取 Web Session
    this.client.on('webSession', (sessionID, cookies) => {
      this.webSession = {
        sessionID,
        cookies,
        steamID: this.client?.steamID?.getSteamID64() || '',
      }
    })

    // 错误处理
    this.client.on('error', (err) => {
      console.error('[Steam Auth] 错误:', err.message)
      this.isLoggedIn = false
    })

    // 断开连接
    this.client.on('disconnected', (_eresult, msg) => {
      console.warn('[Steam Auth] 断开连接:', msg)
      this.isLoggedIn = false
    })

    // Steam Guard 验证码请求
    this.client.on('steamGuard', (domain) => {
      if (domain) {
        console.warn(`[Steam Auth] 验证码已发送到邮箱: ${domain}`)
      }
      else {
        console.warn('[Steam Auth] 请使用移动验证器获取验证码')
      }
    })
  }

  /**
   * 登录 Steam
   */
  async login(options: SteamLoginOptions): Promise<boolean> {
    console.warn(SteamUser.formatCurrency(12.34, SteamUser.ECurrencyCode.USD))

    return new Promise((resolve, reject) => {
      if (!this.client) {
        reject(new Error('Steam client 未初始化'))
        return
      }

      if (this.isLoggedIn) {
        resolve(true)
        return
      }

      const logOnOptions: any = {
        accountName: options.accountName,
        password: options.password,
        rememberPassword: options.rememberPassword ?? true,
      }

      // 添加双因素认证代码
      if (options.twoFactorCode) {
        logOnOptions.twoFactorCode = options.twoFactorCode
      }

      if (options.authCode) {
        logOnOptions.authCode = options.authCode
      }

      // 使用对象来避免提升问题
      const handlers = {
        timeout: null as NodeJS.Timeout | null,
        onLoggedOn: null as (() => void) | null,
        onError: null as ((err: Error) => void) | null,
        cleanup: () => {
          if (handlers.timeout) {
            clearTimeout(handlers.timeout)
          }
          if (handlers.onLoggedOn) {
            this.client?.removeListener('loggedOn', handlers.onLoggedOn)
          }
          if (handlers.onError) {
            this.client?.removeListener('error', handlers.onError)
          }
        },
      }

      // 监听错误
      handlers.onError = (err: Error) => {
        handlers.cleanup()
        reject(err)
      }

      // 监听登录成功
      handlers.onLoggedOn = () => {
        handlers.cleanup()

        // 等待 webSession 事件
        const sessionTimeout = setTimeout(() => {
          reject(new Error('获取 Web Session 超时'))
        }, 10000)

        const onWebSession = () => {
          clearTimeout(sessionTimeout)
          resolve(true)
        }

        this.client?.once('webSession', onWebSession)
      }

      // 设置超时
      handlers.timeout = setTimeout(() => {
        handlers.cleanup()
        reject(new Error('登录超时'))
      }, 30000)

      this.client.once('loggedOn', handlers.onLoggedOn)
      this.client.once('error', handlers.onError)

      // 开始登录
      this.client.logOn(logOnOptions)
    })
  }

  /**
   * 获取 Steam Store Access Token
   */
  async getStoreAccessToken(): Promise<SteamStoreAccessToken> {
    try {
      if (!this.isLoggedIn || !this.webSession) {
        return {
          success: false,
          error: '未登录或未获取到 Web Session',
        }
      }

      // 构建 Cookie 字符串
      const cookieString = this.webSession.cookies.join('; ')

      // 访问 Steam Store API
      const response = await axios.get('https://store.steampowered.com/pointssummary/ajaxgetasyncconfig', {
        headers: {
          'Cookie': cookieString,
          'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
          'Referer': 'https://store.steampowered.com/',
        },
        timeout: 10000,
      })

      // 检查是否成功
      if (response.data && response.data.success === 1) {
        // 从响应中提取 token（根据实际响应结构调整）
        const token = response.data.data?.webapi_token || response.data.webapi_token

        return {
          success: true,
          token: token || JSON.stringify(response.data),
        }
      }

      return {
        success: false,
        error: '未能获取 Access Token',
      }
    }
    catch (error: any) {
      console.error('[Steam Auth] 获取 Access Token 失败:', error.message)
      return {
        success: false,
        error: error.message,
      }
    }
  }

  /**
   * 获取 Web Session 信息
   */
  getWebSession(): SteamWebSession | null {
    return this.webSession
  }

  /**
   * 获取登录状态
   */
  getLoginStatus(): boolean {
    return this.isLoggedIn
  }

  /**
   * 登出
   */
  logout() {
    if (this.client && this.isLoggedIn) {
      this.client.logOff()
      this.isLoggedIn = false
      this.webSession = null
    }
  }

  /**
   * 获取当前登录的 Steam ID
   */
  getSteamID(): string | null {
    return this.client?.steamID?.getSteamID64() || null
  }
}

// 导出单例
export const steamAuthManager = new SteamAuthManager()

// 便捷函数
export async function loginSteam(options: SteamLoginOptions): Promise<boolean> {
  return steamAuthManager.login(options)
}

export async function getSteamStoreAccessToken(): Promise<SteamStoreAccessToken> {
  return steamAuthManager.getStoreAccessToken()
}

export function logoutSteam() {
  steamAuthManager.logout()
}

export function getSteamLoginStatus(): boolean {
  return steamAuthManager.getLoginStatus()
}
