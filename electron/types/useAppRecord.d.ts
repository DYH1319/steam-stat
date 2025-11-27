export interface GetValidRecordsResponse {
  // From use_app_record
  appId: number
  steamId: bigint
  startTime: Date
  endTime: Date | null
  duration: number | null

  // From steam_app
  appName: string | null
  nameLocalized: string | unknown
}
