import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('electron', {
  // 预留Electron API接口
  getProcessList: () => ipcRenderer.invoke('get-process-list'),
})
