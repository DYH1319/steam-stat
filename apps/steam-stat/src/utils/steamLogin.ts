import i18n from '@/i18n'
import dayjs from '@/utils/dayjs.ts'

const t = i18n.global.t

// 已知的登录错误码（与后端 EResult 名称 / 自定义错误码对应），用于本地化错误提示
const KNOWN_LOGIN_ERROR_CODES = new Set([
  'InvalidPassword',
  'RateLimitExceeded',
  'AccountLoginDeniedThrottle',
  'AccessDenied',
  'TwoFactorCodeMismatch',
  'AccountDisabled',
  'ServiceUnavailable',
  'Expired',
  'alreadyInProgress',
  'timeout',
  'networkError',
  'connectionFailed',
  'tokenNotFound',
  'tokenDecryptFailed',
  // 自动重连的终止性错误码
  'Revoked',
  'InvalidSignature',
  'AccountLockedDown',
  'AccountLogonDenied',
  'AccountLoginDeniedNeedTwoFactor',
  'Banned',
  'AccountNotFound',
  'reconnectAttemptsExhausted',
])

// 将登录错误码本地化为用户可读的提示文本
export function localizeSteamLoginError(errorCode?: string, message?: string): string {
  if (errorCode && KNOWN_LOGIN_ERROR_CODES.has(errorCode)) {
    return t(`steamLogin.errors.${errorCode}`)
  }
  if (message) {
    return t('steamLogin.errors.unknownWithMessage', { message })
  }
  return t('steamLogin.errors.unknown')
}

// 检查已保存的 Token 是否已过期（过期时间由后端解析 JWT 得出，凭证内容不会传到渲染进程）
export function isSteamLoginTokenExpired(token: SteamLoginToken): boolean {
  if (!token.expiresAt) {
    return false
  }
  return dayjs.unix(token.expiresAt).isBefore(dayjs())
}

// Persona 状态选项
export function steamPersonaStateOptions() {
  return [
    { label: t('steamLogin.personaState.offline'), value: 0 },
    { label: t('steamLogin.personaState.online'), value: 1 },
    { label: t('steamLogin.personaState.busy'), value: 2 },
    { label: t('steamLogin.personaState.away'), value: 3 },
    { label: t('steamLogin.personaState.snooze'), value: 4 },
    { label: t('steamLogin.personaState.lookingToTrade'), value: 5 },
    { label: t('steamLogin.personaState.lookingToPlay'), value: 6 },
    { label: t('steamLogin.personaState.invisible'), value: 7 },
  ]
}
