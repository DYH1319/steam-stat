import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('electron', {
  // Steam 相关 API
  steamGetLoginUser: () => ipcRenderer.invoke('steam-getLoginUser'),
  steamRefreshLoginUser: () => ipcRenderer.invoke('steam-refreshLoginUser'),
  steamGetStatus: () => ipcRenderer.invoke('steam-getStatus'),
  steamRefreshStatus: () => ipcRenderer.invoke('steam-refreshStatus'),
  steamGetRunningApps: () => ipcRenderer.invoke('steam-getRunningApps'),
  steamGetAppsInfo: () => ipcRenderer.invoke('steam-getAppsInfo'),
  steamRefreshAppsInfo: () => ipcRenderer.invoke('steam-refreshAppsInfo'),
  steamGetLibraryFolders: () => ipcRenderer.invoke('steam-getLibraryFolders'),
  steamGetValidUseAppRecord: () => ipcRenderer.invoke('steam-getValidUseAppRecord'),

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

  // 定时任务相关 API
  jobGetUpdateAppRunningStatusJobStatus: () => ipcRenderer.invoke('job:updateAppRunningStatus:getStatus'),
  jobStartUpdateAppRunningStatusJob: () => ipcRenderer.invoke('job:updateAppRunningStatus:start'),
  jobStopUpdateAppRunningStatusJob: () => ipcRenderer.invoke('job:updateAppRunningStatus:stop'),
  jobSetUpdateAppRunningStatusJobInterval: (intervalSeconds: number) => ipcRenderer.invoke('job:updateAppRunningStatus:setInterval', intervalSeconds),

  // Shell API
  shellOpenExternal: (url: string) => ipcRenderer.invoke('shell:openExternal', url),
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
