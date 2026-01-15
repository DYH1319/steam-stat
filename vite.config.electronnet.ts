import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import dayjs from 'dayjs'
import { defineConfig, loadEnv } from 'vite'
import pkg from './package.json'
import createVitePlugins from './vite/plugins'

// Vite 配置用于 Electron.NET
// 与原配置的主要区别：
// 1. 移除了 vite-plugin-electron（Electron.NET 不需要）
// 2. 添加了 base: './' 以支持 file:// 协议
// 3. 构建输出统一到 dist 目录

// https://vitejs.dev/config/
export default defineConfig(({ mode, command }) => {
  const env = loadEnv(mode, process.cwd())

  // 全局 scss 资源
  const scssResources: string[] = []
  fs.readdirSync('src/assets/styles/resources').forEach((dirname) => {
    if (fs.statSync(`src/assets/styles/resources/${dirname}`).isFile()) {
      scssResources.push(`@use "/src/assets/styles/resources/${dirname}" as *;`)
    }
  })

  return {
    // 🔥 重要：使用相对路径，支持 file:// 协议
    base: './',

    // 开发服务器选项
    server: {
      port: 9000,
      proxy: {
        '/proxy': {
          target: env.VITE_APP_API_BASEURL,
          changeOrigin: command === 'serve' && env.VITE_OPEN_PROXY === 'true',
          rewrite: path => path.replace(/\/proxy/, ''),
        },
      },
    },

    // 构建选项
    build: {
      outDir: 'dist', // 统一输出到 dist
      sourcemap: env.VITE_BUILD_SOURCEMAP === 'true',
      // Electron.NET 推荐配置
      rollupOptions: {
        output: {
          // 减小 chunk 大小
          manualChunks: {
            vendor: ['vue', 'vue-router', 'pinia'],
            ui: ['element-plus'],
          },
        },
      },
    },

    // 依赖优化选项
    optimizeDeps: {
      exclude: [
        // 前端不需要的后端依赖
        'electron',
        'electron-builder',
        'electron-updater',
        '@electron/rebuild',
        'vite-plugin-electron',
        'better-sqlite3',
        'drizzle-orm',
        'drizzle-kit',
        'steam-user',
        'steam-session',
        'winreg',
        'kvparser',
        'protobufjs',
        'esbuild',
      ],
    },

    define: {
      __SYSTEM_INFO__: JSON.stringify({
        pkg: {
          version: pkg.version,
          dependencies: pkg.dependencies,
          devDependencies: pkg.devDependencies,
        },
        lastBuildTime: dayjs().format('YYYY-MM-DD HH:mm:ss'),
      }),
    },

    plugins: [
      ...createVitePlugins(mode, command === 'build'),
      // ⚠️ 注意：不包含 vite-plugin-electron
      // Electron.NET 使用 C# 管理 Electron 进程
    ],

    resolve: {
      alias: {
        '@': path.resolve(__dirname, 'src'),
        '#': path.resolve(__dirname, 'src/types'),
      },
    },

    css: {
      preprocessorOptions: {
        scss: {
          additionalData: scssResources.join(''),
        },
      },
    },
  }
})
