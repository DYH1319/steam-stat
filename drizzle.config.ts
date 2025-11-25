import { defineConfig } from 'drizzle-kit'

export default defineConfig({
  schema: './electron/db/schema.ts', // 你的 schema 文件路径
  out: './drizzle', // 迁移文件输出目录
  dialect: 'sqlite', // 数据库类型
  dbCredentials: {
    url: './electron/db/steam-stat.db', // 数据库文件路径
  },
})
