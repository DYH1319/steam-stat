// noinspection JSUnresolvedReference,JSUnusedGlobalSymbols,NpmUsedModulesInstalled

"use strict";
const electron = require("electron");
electron.contextBridge.exposeInMainWorld("electron", {
  // Steam 相关 API
  steamGetStatus: () => electron.ipcRenderer.invoke("steam:status:get"),
  steamRefreshStatus: () => electron.ipcRenderer.invoke("steam:status:refresh"),
  steamGetLibraryFolders: () => electron.ipcRenderer.invoke("steam:libraryFolders:get"),

  steamGetLoginUser: () => electron.ipcRenderer.invoke("steam:loginUsers:get"),
  steamRefreshLoginUser: () => electron.ipcRenderer.invoke("steam:loginUsers:refresh"),
  onSteamUserUpdatedEvent: (callback) => electron.ipcRenderer.on("steam:loginUsers:updated", (_event, data) => callback(data)),
  removeSteamUserUpdatedEventListener: () => electron.ipcRenderer.removeAllListeners("steam:loginUsers:updated"),

  steamGetRunningApps: () => electron.ipcRenderer.invoke("steam:runningApps:get"),
  steamGetAppsInfo: () => electron.ipcRenderer.invoke("steam:appsInfo:get"),
  steamRefreshAppsInfo: () => electron.ipcRenderer.invoke("steam:appsInfo:refresh"),

  steamGetValidUseAppRecord: (param) => electron.ipcRenderer.invoke("steam:validUseAppRecord:get", param),
  steamGetUsersInRecord: () => electron.ipcRenderer.invoke("steam:usersInRecords:get"),
  steamEndUseAppRecording: () => electron.ipcRenderer.invoke("steam:useAppRecording:end"),
  steamDiscardUseAppRecording: () => electron.ipcRenderer.invoke("steam:useAppRecording:discard"),

  // 定时任务相关 API
  jobGetUpdateAppRunningStatusJobStatus: () => electron.ipcRenderer.invoke("job:updateAppRunningStatus:get"),

  jobStartUpdateAppRunningStatusJob: () => electron.ipcRenderer.invoke("job:updateAppRunningStatus:start"),
  jobStopUpdateAppRunningStatusJob: () => electron.ipcRenderer.invoke("job:updateAppRunningStatus:stop"),
  jobSetUpdateAppRunningStatusJobInterval: (intervalSeconds) => electron.ipcRenderer.invoke("job:updateAppRunningStatus:setInterval", intervalSeconds),

  // Shell 相关 API
  shellOpenExternal: (url) => electron.ipcRenderer.send("shell:openExternal", url),
  shellOpenPath: (path) => electron.ipcRenderer.send("shell:openPath", path),

  // Settings 相关 API
  settingGet: () => electron.ipcRenderer.invoke("setting:get"),
  settingUpdate: (param) => electron.ipcRenderer.invoke("setting:update", param),

  settingsReset: () => electron.ipcRenderer.invoke("settings:reset"),
  settingsSetCloseAction: (action) => electron.ipcRenderer.invoke("settings:setCloseAction", action),

  // Updater 相关 API
  updaterGetStatus: () => electron.ipcRenderer.invoke("updater:status:get"),
  updaterEventOnListener: (callback) => electron.ipcRenderer.on("updater:event", (_event, data) => callback(data)),
  updateEventRemoveListener: () => electron.ipcRenderer.removeAllListeners("updater:event"),

  updateCheckForUpdates: () => electron.ipcRenderer.invoke("update:checkForUpdates"),
  updateDownloadUpdate: () => electron.ipcRenderer.invoke("update:downloadUpdate"),
  updateQuitAndInstall: () => electron.ipcRenderer.invoke("update:quitAndInstall"),
  updateGetCurrentVersion: () => electron.ipcRenderer.invoke("update:getCurrentVersion"),
  updateSetAutoUpdate: (enabled) => electron.ipcRenderer.invoke("update:setAutoUpdate", enabled),

  // 应用窗体相关 API
  appQuit: () => electron.ipcRenderer.send("app:quit"),
  windowMinimizeToTray: () => electron.ipcRenderer.send("window:minimizeToTray"),
  windowMinimize: () => electron.ipcRenderer.send("window:minimize"),
  windowMaximize: () => electron.ipcRenderer.invoke("window:maximize"),
  windowClose: () => electron.ipcRenderer.send("window:close"),
  windowIsMaximized: () => electron.ipcRenderer.invoke("window:isMaximized"),
});
