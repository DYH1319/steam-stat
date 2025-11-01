import { contextBridge } from 'electron'

contextBridge.exposeInMainWorld('electron', {
  // 预留Electron API接口
})
