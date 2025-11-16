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

// 测试 formatBytes 函数
// (async () => console.warn('[Util Test] 格式化字节:', formatBytes(60000)))()

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
