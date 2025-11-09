import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('electron', {
  // 进程监控 API
  getProcessList: () => ipcRenderer.invoke('get-process-list'),

  // Steam API（从数据库获取）
  getSteamUser: () => ipcRenderer.invoke('get-steam-user'),
  getSteamUsers: () => ipcRenderer.invoke('get-steam-users'),
  getSteamGames: () => ipcRenderer.invoke('get-steam-games'),
  getSteamStatus: () => ipcRenderer.invoke('get-steam-status'),

  // Steam API（重新从文件获取）
  refreshSteamUsers: () => ipcRenderer.invoke('refresh-steam-users'),
  refreshSteamGames: () => ipcRenderer.invoke('refresh-steam-games'),

  // 游戏记录 API
  getGameRecords: (appId?: string, limit?: number) => ipcRenderer.invoke('get-game-records', appId, limit),
  getGameTotalPlayTime: (appId: string) => ipcRenderer.invoke('get-game-total-playtime', appId),

  // 监控 API
  startMonitor: (intervalSeconds: number) => ipcRenderer.invoke('start-monitor', intervalSeconds),
  stopMonitor: () => ipcRenderer.invoke('stop-monitor'),
  updateMonitorInterval: (intervalSeconds: number) => ipcRenderer.invoke('update-monitor-interval', intervalSeconds),
  getMonitorStatus: () => ipcRenderer.invoke('get-monitor-status'),

  // Steam 认证 API
  steamLogin: (options: any) => ipcRenderer.invoke('steam-login', options),
  steamLogout: () => ipcRenderer.invoke('steam-logout'),
  steamGetLoginStatus: () => ipcRenderer.invoke('steam-get-login-status'),
  steamGetStoreToken: () => ipcRenderer.invoke('steam-get-store-token'),
})
