import process from 'node:process'

/**
 * Steam 进程列表常量
 */
export const STEAM_PROCESSES = process.platform === 'darwin'
  ? [
      'steam_osx',
      'steamservice',
      'steamwebhelper',
      'GameOverlayUI',
    ]
  : [
      'steam',
      'steamservice',
      'steamwebhelper',
      'GameOverlayUI',
    ]
