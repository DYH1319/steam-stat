export interface AppSettings {
  autoStart: boolean
  autoUpdate: boolean
  updateAppRunningStatusJob: {
    enabled: boolean
    intervalSeconds: number
  }
}
