/**
 * 检查 electron 目录中实际使用的依赖
 */
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const electronDir = path.join(__dirname, '../electron')

// 读取所有 .ts 文件
function getAllTsFiles(dir) {
  const files = []
  const items = fs.readdirSync(dir, { withFileTypes: true })

  for (const item of items) {
    const fullPath = path.join(dir, item.name)
    if (item.isDirectory()) {
      files.push(...getAllTsFiles(fullPath))
    }
    else if (item.isFile() && item.name.endsWith('.ts')) {
      files.push(fullPath)
    }
  }

  return files
}

// 提取 import 语句中的包名
function extractImports(content) {
  const imports = new Set()
  // 匹配 from 'package-name' 或 from "package-name"
  const regex = /from\s+['"]([^'"]+)['"]/g
  let match

  while ((match = regex.exec(content)) !== null) {
    const importPath = match[1]
    // 只保留非相对路径的导入（即第三方包）
    if (!importPath.startsWith('.') && !importPath.startsWith('node:')) {
      // 提取包名（处理 @scope/package 和 package/subpath）
      const parts = importPath.split('/')
      if (importPath.startsWith('@')) {
        imports.add(`${parts[0]}/${parts[1]}`)
      }
      else {
        imports.add(parts[0])
      }
    }
  }

  return imports
}

// 主函数
function main() {
  const files = getAllTsFiles(electronDir)
  const allImports = new Set()

  for (const file of files) {
    const content = fs.readFileSync(file, 'utf-8')
    const imports = extractImports(content)
    imports.forEach(imp => allImports.add(imp))
  }

  console.log('Electron 主进程实际使用的第三方依赖：\n')
  const sorted = Array.from(allImports).sort()
  sorted.forEach(dep => console.log(`  - ${dep}`))

  console.log(`\n总计：${sorted.length} 个依赖\n`)

  // 读取 package.json 的 dependencies
  const pkg = JSON.parse(fs.readFileSync(path.join(__dirname, '../package.json'), 'utf-8'))
  const currentDeps = Object.keys(pkg.dependencies)

  console.log('\n当前 dependencies 中可能不需要的包（未在 electron 中使用）：\n')
  const unused = currentDeps.filter(dep => !sorted.includes(dep))
  unused.forEach(dep => console.log(`  - ${dep}`))

  console.log(`\n可移除：${unused.length} 个依赖`)
}

main()
