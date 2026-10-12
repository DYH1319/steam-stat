import antfu from '@antfu/eslint-config'

export default antfu(
  {
    vue: true,
    unocss: true,
    markdown: false,
    ignores: [
      '**/public',
      '**/dist*',
      '**/preload.mjs',
      // 第三方源码（git submodule）
      'third_party',
      // .NET 构建产物
      '**/bin/**',
      '**/obj/**',
      'ElectronNet/**/Publish/**',
    ],
  },
  {
    rules: {
      'e18e/prefer-static-regex': 'off',
      'eslint-comments/no-unlimited-disable': 'off',
      'curly': ['error', 'all'],
      'ts/no-unused-expressions': ['error', {
        allowShortCircuit: true,
        allowTernary: true,
      }],
    },
  },
  {
    files: [
      'src/**/*.vue',
    ],
    rules: {
      'vue/block-order': ['error', {
        order: ['script', 'template', 'style'],
      }],
    },
  },
  {
    files: [
      'pnpm-workspace.yaml',
    ],
    rules: {
      'pnpm/yaml-enforce-settings': 'off',
    },
  },
  {
    // steam-stat 应用专属依赖需精确锁定版本，不进入框架 catalog
    files: [
      'apps/steam-stat/package.json',
    ],
    rules: {
      'pnpm/json-enforce-catalog': 'off',
    },
  },
)
