import SteamID from 'steamid'

export function hexToDecNum(val: string | undefined): number {
  if (val && /^0x/i.test(val)) {
    return Number.parseInt(val, 16)
  }
  return Number.parseInt(val ?? '0')
}

export function hexToDecStr(val: string | undefined): string {
  return hexToDecNum(val)?.toString()
}

export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB', 'ZB', 'YB']
  const index = Math.floor(Math.log(bytes) / Math.log(1024))
  return `${(bytes / 1024 ** index).toFixed(2)} ${units[index]}`
}

export function getRegValue(regKey: Winreg.Registry, name: string): Promise<string | undefined> {
  return new Promise((resolve) => {
    regKey.get(name, (err, item) => {
      if (err || !item) {
        return resolve(undefined)
      }
      resolve(item.value)
    })
  })
}

export function steamIdToAccountId(steamId: bigint | string): number {
  if (steamId === 0n || steamId === '0') {
    return 0
  }
  return new SteamID(steamId).accountid
}

export function accountIdToSteamId(accountId: number): bigint {
  if (accountId === 0) {
    return 0n
  }
  return SteamID.fromIndividualAccountID(accountId).getBigIntID()
}

export function accountIdToSteamIdStr(accountId: number): string {
  if (accountId === 0) {
    return '0'
  }
  return SteamID.fromIndividualAccountID(accountId).getSteamID64()
}
