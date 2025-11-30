import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import dayjs from 'dayjs'
import { defineConfig, loadEnv } from 'vite'
import electron from 'vite-plugin-electron/simple'
import pkg from './package.json'
import createVitePlugins from './vite/plugins'

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
    // 开发服务器选项 https://cn.vitejs.dev/config/server-options
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
    // 构建选项 https://cn.vitejs.dev/config/build-options
    build: {
      outDir: mode === 'production' ? 'dist' : `dist-${mode}`,
      sourcemap: env.VITE_BUILD_SOURCEMAP === 'true',
    },
    // 依赖优化选项 https://cn.vitejs.dev/config/dep-optimization-options
    optimizeDeps: {
      exclude: [
        // Electron 相关
        'electron',
        'electron-builder',
        'electron-updater',
        '@electron/rebuild',
        'vite-plugin-electron',
        // 原生模块和仅后端使用的依赖
        'better-sqlite3',
        'drizzle-orm',
        'drizzle-kit',
        'steam-user',
        'steam-session',
        'winreg',
        'kvparser',
        'protobufjs',
        // 其他构建工具
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
      electron({
        main: {
          entry: 'electron/main.ts',
          vite: {
            build: {
              // sourcemap: true, // ✅ 关键！启用 source map
              watch: null, // ✅ 直接禁用监听
              rollupOptions: {
                external: [
                  'steam-user',
                  'steam-session',
                  'better-sqlite3',
                  // 'ws',
                  // 👆 这里加上所有使用了 __dirname 的 CJS 库
                ],
              },
            },
          },
        },
        preload: {
          input: 'electron/preload.ts',
          vite: {
            build: {
              watch: null, // ✅ 禁用 preload 监听
            },
          },
        },
      }),
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
