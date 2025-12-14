export interface AppSettings {
  autoStart: boolean
  autoUpdate: boolean
  language: string
  closeAction: 'exit' | 'minimize' | 'ask'
  updateAppRunningStatusJob: {
    enabled: boolean
    intervalSeconds: number
  }
}
