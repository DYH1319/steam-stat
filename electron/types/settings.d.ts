export interface AppSettings {
  autoStart: boolean
  autoUpdate: boolean
  language: string
  updateAppRunningStatusJob: {
    enabled: boolean
    intervalSeconds: number
  }
}
