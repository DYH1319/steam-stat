<script setup lang="ts">
import { ref } from 'vue'

interface LoginForm {
  accountName: string
  password: string
  twoFactorCode: string
  authCode: string
  rememberPassword: boolean
}

// Steam 登录表单
const loginForm = ref<LoginForm>({
  accountName: '',
  password: '',
  twoFactorCode: '',
  authCode: '',
  rememberPassword: true,
})

// 登录状态
const isLoggedIn = ref(false)
const isLoading = ref(false)
const errorMessage = ref('')
const storeToken = ref('')

// 获取 Electron API
const electronApi = (window as any).electron

/**
 * 登录 Steam
 */
async function handleLogin() {
  if (!electronApi?.steamLogin) {
    errorMessage.value = 'Electron API 不可用'
    return
  }

  errorMessage.value = ''
  isLoading.value = true

  try {
    const result = await electronApi.steamLogin({
      accountName: loginForm.value.accountName,
      password: loginForm.value.password,
      twoFactorCode: loginForm.value.twoFactorCode || undefined,
      authCode: loginForm.value.authCode || undefined,
      rememberPassword: loginForm.value.rememberPassword,
    })

    if (result.success) {
      isLoggedIn.value = true

      // 自动获取 Store Token
      await getStoreToken()
    }
    else {
      errorMessage.value = result.error || '登录失败'
      console.error('[Steam Auth] 登录失败:', result.error)
    }
  }
  catch (error: any) {
    errorMessage.value = error.message
    console.error('[Steam Auth] 登录错误:', error)
  }
  finally {
    isLoading.value = false
  }
}

/**
 * 登出
 */
async function handleLogout() {
  if (!electronApi?.steamLogout) {
    return
  }

  isLoading.value = true

  try {
    await electronApi.steamLogout()
    isLoggedIn.value = false
    storeToken.value = ''
  }
  catch (error) {
    console.error('[Steam Auth] 登出错误:', error)
  }
  finally {
    isLoading.value = false
  }
}

/**
 * 检查登录状态
 */
async function checkLoginStatus() {
  if (!electronApi?.steamGetLoginStatus) {
    return
  }

  try {
    isLoggedIn.value = await electronApi.steamGetLoginStatus()
  }
  catch (error) {
    console.error('[Steam Auth] 获取登录状态失败:', error)
  }
}

/**
 * 获取 Store Access Token
 */
async function getStoreToken() {
  if (!electronApi?.steamGetStoreToken) {
    errorMessage.value = 'Electron API 不可用'
    return
  }

  isLoading.value = true
  errorMessage.value = ''

  try {
    const result = await electronApi.steamGetStoreToken()

    if (result.success) {
      storeToken.value = result.token || ''
    }
    else {
      errorMessage.value = result.error || '获取 Token 失败'
      console.error('[Steam Auth] 获取 Token 失败:', result.error)
    }
  }
  catch (error: any) {
    errorMessage.value = error.message
    console.error('[Steam Auth] 获取 Token 错误:', error)
  }
  finally {
    isLoading.value = false
  }
}

// 组件挂载时检查登录状态
checkLoginStatus()
</script>

<template>
  <div class="steam-auth-container">
    <el-card class="auth-card">
      <template #header>
        <div class="card-header">
          <h2>Steam 认证管理</h2>
          <el-tag v-if="isLoggedIn" type="success">
            已登录
          </el-tag>
          <el-tag v-else type="info">
            未登录
          </el-tag>
        </div>
      </template>

      <!-- 未登录 - 显示登录表单 -->
      <div v-if="!isLoggedIn" class="login-form">
        <el-form :model="loginForm" label-width="120px">
          <el-form-item label="账号名称" required>
            <el-input
              v-model="loginForm.accountName"
              placeholder="请输入 Steam 账号"
              :disabled="isLoading"
            />
          </el-form-item>

          <el-form-item label="密码" required>
            <el-input
              v-model="loginForm.password"
              type="password"
              placeholder="请输入密码"
              :disabled="isLoading"
              show-password
            />
          </el-form-item>

          <el-form-item label="移动验证码">
            <el-input
              v-model="loginForm.twoFactorCode"
              placeholder="5位验证码（如有）"
              maxlength="5"
              :disabled="isLoading"
            />
          </el-form-item>

          <el-form-item label="邮箱验证码">
            <el-input
              v-model="loginForm.authCode"
              placeholder="邮箱验证码（如有）"
              :disabled="isLoading"
            />
          </el-form-item>

          <el-form-item label="记住密码">
            <el-switch
              v-model="loginForm.rememberPassword"
              :disabled="isLoading"
            />
          </el-form-item>

          <el-form-item>
            <el-button
              type="primary"
              :loading="isLoading"
              @click="handleLogin"
            >
              登录
            </el-button>
            <el-button @click="checkLoginStatus">
              检查登录状态
            </el-button>
          </el-form-item>
        </el-form>

        <el-alert
          v-if="errorMessage"
          :title="errorMessage"
          type="error"
          show-icon
          style="margin-top: 16px;"
        />

        <el-alert
          title="提示"
          type="warning"
          :closable="false"
          style="margin-top: 16px;"
        >
          <p>⚠️ 请注意:</p>
          <ul style="padding-left: 20px; margin: 8px 0;">
            <li>建议使用测试账号，不要使用主账号</li>
            <li>如果开启了 Steam Guard，需要提供验证码</li>
            <li>自动化登录可能违反 Steam 服务条款，请谨慎使用</li>
          </ul>
        </el-alert>
      </div>

      <!-- 已登录 - 显示 Token 信息 -->
      <div v-else class="token-info">
        <el-descriptions :column="1" border>
          <el-descriptions-item label="登录状态">
            <el-tag type="success">
              已登录
            </el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="Store Access Token">
            <el-input
              v-model="storeToken"
              readonly
              type="textarea"
              :rows="4"
              placeholder="点击下方按钮获取 Token"
            />
          </el-descriptions-item>
        </el-descriptions>

        <div style="display: flex; gap: 8px; margin-top: 16px;">
          <el-button
            type="primary"
            :loading="isLoading"
            @click="getStoreToken"
          >
            获取 Store Token
          </el-button>
          <el-button
            type="danger"
            :loading="isLoading"
            @click="handleLogout"
          >
            登出
          </el-button>
        </div>

        <el-alert
          v-if="errorMessage"
          :title="errorMessage"
          type="error"
          show-icon
          style="margin-top: 16px;"
        />

        <el-alert
          v-if="storeToken"
          title="成功获取 Store Access Token"
          type="success"
          show-icon
          style="margin-top: 16px;"
        >
          <p>Token 已成功获取，可以用于访问 Steam Store API</p>
        </el-alert>
      </div>
    </el-card>
  </div>
</template>

<style scoped lang="scss">
.steam-auth-container {
  max-width: 800px;
  padding: 20px;
  margin: 0 auto;
}

.auth-card {
  margin-top: 20px;
}

.card-header {
  display: flex;
  align-items: center;
  justify-content: space-between;

  h2 {
    margin: 0;
    font-size: 20px;
  }
}

.login-form {
  max-width: 600px;

  ul {
    padding-left: 20px;
    margin: 8px 0;
  }
}

.token-info {
  :deep(.el-textarea__inner) {
    font-family: monospace;
  }

  div {
    display: flex;
    gap: 8px;
    margin-top: 16px;
  }
}
</style>
