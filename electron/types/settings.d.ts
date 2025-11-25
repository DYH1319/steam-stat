export interface AppSettings {
  autoStart: boolean
  updateAppRunningStatusJob: {
    enabled: boolean
    intervalSeconds: number
  }
}
