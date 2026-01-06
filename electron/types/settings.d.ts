export interface AppSettings {
  autoStart: boolean
  silentStart: boolean
  autoUpdate: boolean
  language: string
  closeAction: 'exit' | 'minimize' | 'ask'
  updateAppRunningStatusJob: {
    enabled: boolean
    intervalSeconds: number
  }
}
