import os from 'node:os'
import path from 'node:path'
import process from 'node:process'
import { defineConfig } from 'drizzle-kit'

// 获取实际的数据库路径
// 运行时路径: C:\Users\<用户名>\AppData\Roaming\steam-stat\Database\steam-stat.db
function getUserDataPath() {
  const homeDir = os.homedir()
  if (process.platform === 'win32') {
    return path.join(homeDir, 'AppData', 'Roaming', 'steam-stat', 'Database', 'steam-stat.db')
  }
  else if (process.platform === 'darwin') {
    return path.join(homeDir, 'Library', 'Application Support', 'steam-stat', 'Database', 'steam-stat.db')
  }
  else {
    return path.join(homeDir, '.config', 'steam-stat', 'Database', 'steam-stat.db')
  }
}

export default defineConfig({
  schema: './electron/db/schema.ts',
  out: './drizzle',
  dialect: 'sqlite',
  dbCredentials: {
    url: getUserDataPath(),
  },
})
