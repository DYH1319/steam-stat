import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('electron', {
  // Steam Test API
  steamTestGetLoginUser: () => ipcRenderer.invoke('steam-test-getLoginUser'),
  steamTestRefreshLoginUser: () => ipcRenderer.invoke('steam-test-refreshLoginUser'),
  steamTestGetStatus: () => ipcRenderer.invoke('steam-test-getStatus'),
  steamTestRefreshStatus: () => ipcRenderer.invoke('steam-test-refreshStatus'),
  steamTestGetRunningApps: () => ipcRenderer.invoke('steam-test-getRunningApps'),
  steamTestGetInstalledApps: () => ipcRenderer.invoke('steam-test-getInstalledApps'),
  steamTestGetLibraryFolders: () => ipcRenderer.invoke('steam-test-getLibraryFolders'),

  // Steam 账号密码登录
  steamLoginAccountStart: (params: {
    accountName: string
    password: string
    steamGuardMachineToken?: string
  }) => ipcRenderer.invoke('steam:login:account:start', params),

  steamLoginAccountSubmitCode: (params: {
    sessionId: string
    code: string
  }) => ipcRenderer.invoke('steam:login:account:submitCode', params),

  steamLoginAccountCancel: (sessionId: string) =>
    ipcRenderer.invoke('steam:login:account:cancel', sessionId),

  // Steam 二维码登录
  steamLoginQRStart: (httpProxy?: string) =>
    ipcRenderer.invoke('steam:login:qr:start', httpProxy),

  steamLoginQRCancel: (sessionId: string) =>
    ipcRenderer.invoke('steam:login:qr:cancel', sessionId),

  // 监听登录事件
  onSteamLoginEvent: (callback: (event: any) => void) => {
    ipcRenderer.on('steam-login-event', (_event, data) => callback(data))
  },

  onSteamQRLoginEvent: (callback: (event: any) => void) => {
    ipcRenderer.on('steam-qr-login-event', (_event, data) => callback(data))
  },

  // 移除监听器
  removeSteamLoginEventListener: () => {
    ipcRenderer.removeAllListeners('steam-login-event')
  },

  removeSteamQRLoginEventListener: () => {
    ipcRenderer.removeAllListeners('steam-qr-login-event')
  },
})

// TypeScript 类型定义
export interface ElectronAPI {
  // ...现有的类型定义...

  steamLoginAccountStart: (params: {
    accountName: string
    password: string
    steamGuardMachineToken?: string
  }) => Promise<any>

  steamLoginAccountSubmitCode: (params: {
    sessionId: string
    code: string
  }) => Promise<any>

  steamLoginAccountCancel: (sessionId: string) => Promise<any>

  steamLoginQRStart: (httpProxy?: string) => Promise<any>

  steamLoginQRCancel: (sessionId: string) => Promise<any>

  onSteamLoginEvent: (callback: (event: any) => void) => void

  onSteamQRLoginEvent: (callback: (event: any) => void) => void

  removeSteamLoginEventListener: () => void

  removeSteamQRLoginEventListener: () => void
}

declare global {
  interface Window {
    electron: ElectronAPI
  }
}
