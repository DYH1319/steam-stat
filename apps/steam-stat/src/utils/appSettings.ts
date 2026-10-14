/**
 * setting:get 的并发去重封装。
 *
 * Electron.NET 宿主对同一 channel 的并发 invoke 会互相清除回复监听
 *（third_party/Electron.NET src/ElectronNET.Host/api/ipc.ts 中每个
 * registerHandleIpcMainChannel 回调都会先 removeAllListeners(channel + 'Handle')），
 * 导致先发的 invoke 永久 pending，之后在页面刷新/导航时被拒为
 * "Error invoking remote method 'setting:get': reply was never sent"。
 * 这里共享同一个 in-flight promise，保证同一时刻最多一个 setting:get 在途；
 * promise settle 后即失效，后续调用仍会读取到最新设置。
 */
let pendingGet: Promise<AppSettings> | null = null

export function getAppSettings(): Promise<AppSettings> {
  const electronApi = (window as Window).electron
  if (!electronApi) {
    return Promise.reject(new Error('window.electron is unavailable'))
  }
  if (!pendingGet) {
    pendingGet = electronApi.settingGet().finally(() => {
      pendingGet = null
    })
  }
  return pendingGet
}
