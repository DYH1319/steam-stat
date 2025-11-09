/**
 * Steam 认证功能使用示例
 * 演示如何使用 steam-user 登录并获取 Store Access Token
 */

import { getSteamStoreAccessToken, loginSteam, logoutSteam } from './auth'

/**
 * 示例 1: 基本登录流程
 */
async function example1() {
  console.warn('=== 示例 1: 基本登录流程 ===')

  try {
    // 登录 Steam
    const success = await loginSteam({
      accountName: 'your_username',
      password: 'your_password',
    })

    if (success) {
      console.warn('✅ 登录成功!')

      // 获取 Store Access Token
      const result = await getSteamStoreAccessToken()

      if (result.success) {
        console.warn('✅ 成功获取 Store Access Token:')
        console.warn(result.token)
      }
      else {
        console.error('❌ 获取 Token 失败:', result.error)
      }

      // 登出
      logoutSteam()
      console.warn('✅ 已登出')
    }
  }
  catch (error) {
    console.error('❌ 错误:', error)
  }
}

/**
 * 示例 2: 使用 Steam Guard 移动验证器登录
 */
async function example2() {
  console.warn('=== 示例 2: 使用移动验证器登录 ===')

  try {
    const success = await loginSteam({
      accountName: 'your_username',
      password: 'your_password',
      twoFactorCode: 'XXXXX', // 5位移动验证器代码
    })

    if (success) {
      console.warn('✅ 登录成功!')

      // 获取 Token
      const result = await getSteamStoreAccessToken()
      console.warn('Token 结果:', result)

      logoutSteam()
    }
  }
  catch (error) {
    console.error('❌ 错误:', error)
  }
}

/**
 * 示例 3: 使用邮箱验证码登录
 */
async function example3() {
  console.warn('=== 示例 3: 使用邮箱验证码登录 ===')

  try {
    const success = await loginSteam({
      accountName: 'your_username',
      password: 'your_password',
      authCode: 'XXXXX', // 邮箱收到的验证码
    })

    if (success) {
      console.warn('✅ 登录成功!')

      // 获取 Token
      const result = await getSteamStoreAccessToken()
      console.warn('Token 结果:', result)

      logoutSteam()
    }
  }
  catch (error) {
    console.error('❌ 错误:', error)
  }
}

/**
 * 示例 4: 完整的错误处理
 */
async function example4() {
  console.warn('=== 示例 4: 完整的错误处理 ===')

  try {
    console.warn('正在登录...')

    const success = await loginSteam({
      accountName: 'your_username',
      password: 'your_password',
      rememberPassword: true,
    })

    if (!success) {
      throw new Error('登录失败')
    }

    console.warn('✅ 登录成功，正在获取 Access Token...')

    const result = await getSteamStoreAccessToken()

    if (!result.success) {
      throw new Error(`获取 Token 失败: ${result.error}`)
    }

    console.warn('✅ 成功获取 Access Token:')
    console.warn('Token:', result.token)

    // 使用 token 进行其他操作...
    // ...

    console.warn('操作完成，正在登出...')
    logoutSteam()
    console.warn('✅ 已安全登出')
  }
  catch (error: any) {
    console.error('❌ 发生错误:', error.message)

    // 确保即使发生错误也要登出
    logoutSteam()
  }
}

// 导出示例函数
export {
  example1,
  example2,
  example3,
  example4,
}

// 如果直接运行此文件，执行示例
if (require.main === module) {
  console.warn('⚠️  请将 your_username 和 your_password 替换为实际的 Steam 账号信息')
  console.warn('⚠️  建议使用测试账号，不要使用主账号')
  console.warn('')

  // 取消注释以运行示例
  // example1()
  // example2()
  // example3()
  // example4()
}
