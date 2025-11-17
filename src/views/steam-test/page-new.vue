<route lang="yaml">
meta:
  title: Steam 测试（新）
  icon: i-mdi:flask-outline
</route>

<script setup lang="ts">
import { ref } from 'vue'
import { toast } from 'vue-sonner'

const electronApi = (window as any).electron
const testResults = ref<Record<string, any>>({
  loginUser: null,
  steamStatus: null,
  runningApps: null,
  installedApps: null,
  libraryFolders: null,
})
const loading = ref<Record<string, boolean>>({
  loginUser: false,
  steamStatus: false,
  runningApps: false,
  installedApps: false,
  libraryFolders: false,
})

// 复制到剪贴板
function copyToClipboard(text: string) {
  navigator.clipboard.writeText(text)
  toast.success('已复制到剪贴板')
}
// 格式化字节大小
function formatBytes(bytes: bigint | number | null | undefined): string {
  if (!bytes) {
    return '0 B'
  }
  const value = typeof bytes === 'bigint' ? Number(bytes) : bytes
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const k = 1024
  const i = Math.floor(Math.log(value) / Math.log(k))
  return `${(value / k ** i).toFixed(2)} ${units[i]}`
}
// 1. 登录用户
async function doLoginUser() {
  loading.value.loginUser = true
  try {
    testResults.value.loginUser = await electronApi.steamTestGetLoginUser()
    toast.success('获取登录用户信息成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.loginUser = false
  }
}
function clearLoginUser() {
  testResults.value.loginUser = null
}
// 刷新登录用户
async function refreshLoginUser() {
  loading.value.loginUser = true
  try {
    testResults.value.loginUser = await electronApi.steamTestRefreshLoginUser()
    toast.success('刷新登录用户信息成功')
  }
  catch (e: any) {
    toast.error(`刷新失败: ${e?.message || e}`)
  }
  finally {
    loading.value.loginUser = false
  }
}
// 2. Steam状态
async function doSteamStatus() {
  loading.value.steamStatus = true
  try {
    testResults.value.steamStatus = await electronApi.steamTestGetStatus()
    toast.success('获取Steam状态成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.steamStatus = false
  }
}
function clearSteamStatus() {
  testResults.value.steamStatus = null
}
// 刷新Steam状态
async function refreshSteamStatus() {
  loading.value.steamStatus = true
  try {
    testResults.value.steamStatus = await electronApi.steamTestRefreshStatus()
    toast.success('刷新Steam状态成功')
  }
  catch (e: any) {
    toast.error(`刷新失败: ${e?.message || e}`)
  }
  finally {
    loading.value.steamStatus = false
  }
}
// 3. 运行中的应用
async function doRunningApps() {
  loading.value.runningApps = true
  try {
    testResults.value.runningApps = await electronApi.steamTestGetRunningApps()
    toast.success('获取运行中应用成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.runningApps = false
  }
}
function clearRunningApps() {
  testResults.value.runningApps = null
}
// 4. 已安装的应用
async function doInstalledApps() {
  loading.value.installedApps = true
  try {
    testResults.value.installedApps = await electronApi.steamTestGetInstalledApps()
    toast.success('获取已安装应用成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.installedApps = false
  }
}
function clearInstalledApps() {
  testResults.value.installedApps = null
}
// 5. 游戏库目录
async function doLibraryFolders() {
  loading.value.libraryFolders = true
  try {
    testResults.value.libraryFolders = await electronApi.steamTestGetLibraryFolders()
    toast.success('获取游戏库目录成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.libraryFolders = false
  }
}
function clearLibraryFolders() {
  testResults.value.libraryFolders = null
}
</script>

<template>
  <div>
    <FaPageHeader title="Steam 测试（新）" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 1. 登录用户信息 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              1. 获取Steam本机登录用户信息
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.loginUser" @click="doLoginUser">
                执行测试
              </el-button>
              <el-button type="success" :loading="loading.loginUser" :disabled="!testResults.loginUser" @click="refreshLoginUser">
                <span class="i-mdi:refresh inline-block h-4 w-4" />
                刷新数据
              </el-button>
              <el-button :disabled="!testResults.loginUser" @click="clearLoginUser">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.loginUser" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <template v-if="Array.isArray(testResults.loginUser) && testResults.loginUser.length > 0">
              <el-tag size="large" type="success" class="mb-4">
                共 {{ testResults.loginUser.length }} 个已登录的用户
              </el-tag>
              <div class="grid grid-cols-1 gap-4 lg:grid-cols-2 xl:grid-cols-3">
                <div
                  v-for="user in testResults.loginUser"
                  :key="user.steamId"
                  class="border rounded-lg bg-white p-4 shadow-sm transition-all dark:bg-gray-900 hover:shadow-md"
                >
                  <div class="mb-3 flex items-center justify-between">
                    <div class="flex items-center gap-2">
                      <span class="i-mdi:account-circle inline-block h-8 w-8 text-primary" />
                      <div>
                        <div class="text-lg font-semibold">
                          {{ user.personaName || user.accountName }}
                        </div>
                        <div class="text-sm text-gray-500">
                          @{{ user.accountName }}
                        </div>
                      </div>
                    </div>
                    <el-tag v-if="user.rememberPassword" type="success" size="small">
                      <span class="i-mdi:lock-check inline-block h-3 w-3" /> 已保存密码
                    </el-tag>
                  </div>
                  <el-descriptions :column="1" size="small" border>
                    <el-descriptions-item label="Steam ID">
                      <span class="text-xs font-mono">{{ user.steamId?.toString() }}</span>
                    </el-descriptions-item>
                    <el-descriptions-item label="Account ID">
                      <span class="text-xs font-mono">{{ user.accountId }}</span>
                    </el-descriptions-item>
                    <el-descriptions-item label="更新时间">
                      <span class="text-xs">{{ new Date(user.refreshTime).toLocaleString() }}</span>
                    </el-descriptions-item>
                  </el-descriptions>
                </div>
              </div>
            </template>
            <template v-else>
              <el-empty description="暂无用户数据" />
            </template>
          </div>
        </div>
        <!-- Steam状态 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              2. 获取Steam状态
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.steamStatus" @click="doSteamStatus">
                执行测试
              </el-button>
              <el-button type="success" :loading="loading.steamStatus" :disabled="!testResults.steamStatus" @click="refreshSteamStatus">
                <span class="i-mdi:refresh inline-block h-4 w-4" />
                刷新数据
              </el-button>
              <el-button :disabled="!testResults.steamStatus" @click="clearSteamStatus">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.steamStatus" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-4 flex flex-wrap items-center gap-2">
              <el-tag
                size="large"
                :type="Number(testResults.steamStatus.steamPid) > 0 ? 'success' : 'info'"
              >
                <span class="i-mdi:steam inline-block h-4 w-4" />
                {{ Number(testResults.steamStatus.steamPid) > 0 ? 'Steam 正在运行' : 'Steam 未运行' }}
                <span v-if="Number(testResults.steamStatus.steamPid) > 0"> (PID: {{ testResults.steamStatus.steamPid }})</span>
              </el-tag>
              <el-tag
                size="large"
                :type="Number(testResults.steamStatus.activeUserSteamId) > 0 ? 'primary' : 'info'"
              >
                <span class="i-mdi:account inline-block h-4 w-4" />
                {{ Number(testResults.steamStatus.activeUserSteamId) > 0 ? '用户' : '无登录用户' }}
                <span v-if="Number(testResults.steamStatus.activeUserSteamId) > 0"> (Steam ID: {{ testResults.steamStatus.activeUserSteamId }})</span>
              </el-tag>
              <el-tag
                size="large"
                :type="Number(testResults.steamStatus.runningAppId) > 0 ? 'warning' : 'info'"
              >
                <span class="i-mdi:gamepad inline-block h-4 w-4" />
                {{ Number(testResults.steamStatus.runningAppId) > 0 ? 'Steam对外展示的正在运行的应用 ID' : '无运行应用' }}
                <span v-if="Number(testResults.steamStatus.runningAppId) > 0"> (APP ID: {{ testResults.steamStatus.runningAppId }})</span>
              </el-tag>
              <el-tag v-if="testResults.steamStatus.refreshTime" size="large" type="info">
                <span class="i-mdi:clock inline-block h-4 w-4" />
                数据更新时间: {{ new Date(testResults.steamStatus.refreshTime).toLocaleString() }}
              </el-tag>
            </div>
            <div class="space-y-3">
              <!-- 安装路径 -->
              <div v-if="testResults.steamStatus.steamPath" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:folder inline-block h-5 w-5 text-blue-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">安装路径</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamPath }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamPath)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- 可执行文件 -->
              <div v-if="testResults.steamStatus.steamExePath" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:application inline-block h-5 w-5 text-green-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">可执行文件</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamExePath }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamExePath)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- SteamClient DLL -->
              <div v-if="testResults.steamStatus.steamClientDllPath" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:file-code inline-block h-5 w-5 text-purple-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">SteamClient DLL (32位)</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamClientDllPath }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamClientDllPath)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- SteamClient DLL 64位 -->
              <div v-if="testResults.steamStatus.steamClientDll64Path" class="border rounded-lg bg-white p-3 dark:bg-gray-900">
                <div class="mb-2 flex items-center gap-2">
                  <span class="i-mdi:file-code inline-block h-5 w-5 text-orange-500" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">SteamClient DLL (64位)</span>
                </div>
                <div class="flex items-center gap-2 pl-7">
                  <code class="flex-1 rounded bg-gray-100 px-2 py-1 text-sm dark:bg-gray-800">{{ testResults.steamStatus.steamClientDll64Path }}</code>
                  <el-button size="small" text @click="copyToClipboard(testResults.steamStatus.steamClientDll64Path)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </div>
              <!-- 无数据提示 -->
              <div v-if="!testResults.steamStatus.steamPath && !testResults.steamStatus.steamExePath && !testResults.steamStatus.steamClientDllPath && !testResults.steamStatus.steamClientDll64Path">
                <el-empty description="暂无路径信息" :image-size="80" />
              </div>
            </div>
          </div>
        </div>
        <!-- 运行中应用 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              3. 获取运行中的Steam应用
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.runningApps" @click="doRunningApps">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.runningApps" @click="clearRunningApps">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.runningApps" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" :type="testResults.runningApps.length > 0 ? 'success' : 'info'">
              <span class="i-mdi:gamepad-variant inline-block h-4 w-4" />
              {{ testResults.runningApps.length > 0 ? `${testResults.runningApps.length} 个应用运行中` : '当前无运行应用' }}
            </el-tag>
            <div v-if="testResults.runningApps.length > 0" class="grid grid-cols-1 gap-3 lg:grid-cols-3 md:grid-cols-2">
              <div
                v-for="app in testResults.runningApps"
                :key="app.appId"
                class="border rounded-lg bg-white p-3 shadow-sm dark:bg-gray-900"
              >
                <div class="flex items-center gap-2">
                  <span class="i-mdi:gamepad-variant text-success inline-block h-6 w-6" />
                  <div class="flex-1">
                    <div class="font-semibold">
                      {{ app.name }}
                    </div>
                    <div class="text-xs text-gray-500">
                      App ID: {{ app.appId }}
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
        <!-- 已安装应用 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              4. 获取已安装的Steam应用
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.installedApps" @click="doInstalledApps">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.installedApps" @click="clearInstalledApps">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.installedApps" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" type="success">
              <span class="i-mdi:folder-download inline-block h-4 w-4" />
              共 {{ testResults.installedApps.length }} 个已安装应用
            </el-tag>
            <el-table :data="testResults.installedApps" stripe max-height="500">
              <el-table-column prop="appId" label="App ID" width="100" sortable fixed />
              <el-table-column prop="name" label="应用名称" min-width="200" sortable show-overflow-tooltip>
                <template #default="{ row }">
                  <div class="flex items-center gap-2">
                    <el-tag v-if="row.isRunning" type="success" size="small" effect="dark">
                      <span class="i-mdi:play inline-block h-3 w-3" />
                      运行中
                    </el-tag>
                    <span>{{ row.name || '-' }}</span>
                  </div>
                </template>
              </el-table-column>
              <el-table-column label="本地化名称" min-width="120" show-overflow-tooltip>
                <template #default="{ row }">
                  <span v-if="row.nameLocalized && row.nameLocalized.length > 0" class="text-sm text-gray-600 dark:text-gray-400">
                    {{ row.nameLocalized.join(', ') }}
                  </span>
                  <span v-else class="text-sm text-gray-400">-</span>
                </template>
              </el-table-column>
              <el-table-column prop="installDir" label="安装目录名" min-width="150" sortable show-overflow-tooltip />
              <el-table-column label="安装路径" min-width="200" show-overflow-tooltip>
                <template #default="{ row }">
                  <span class="text-xs font-mono">{{ row.installDirPath || '-' }}</span>
                </template>
              </el-table-column>
              <el-table-column label="占用空间" width="130" sortable align="right">
                <template #default="{ row }">
                  <div class="flex flex-col items-end">
                    <span v-if="row.appOnDisk" class="text-xs font-mono">
                      {{ formatBytes(row.appOnDisk) }}
                    </span>
                    <span v-if="row.appOnDiskReal && row.appOnDiskReal !== row.appOnDisk" class="text-xs text-gray-500 font-mono">
                      实际: {{ formatBytes(row.appOnDiskReal) }}
                    </span>
                  </div>
                </template>
              </el-table-column>
              <el-table-column label="状态" width="90" align="center">
                <template #default="{ row }">
                  <el-tag :type="row.installed ? 'success' : 'info'" size="small">
                    {{ row.installed ? '已安装' : '未安装' }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column label="更新时间" width="160" sortable>
                <template #default="{ row }">
                  <span class="text-xs">{{ row.refreshTime ? new Date(row.refreshTime).toLocaleString() : '-' }}</span>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
        <!-- 游戏库目录 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              5. 获取Steam库目录
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.libraryFolders" @click="doLibraryFolders">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.libraryFolders" @click="clearLibraryFolders">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.libraryFolders && testResults.libraryFolders.length" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" type="success">
              <span class="i-mdi:folder-multiple inline-block h-4 w-4" />
              共 {{ testResults.libraryFolders.length }} 个库目录
            </el-tag>
            <div class="space-y-2">
              <div
                v-for="(folder, idx) in testResults.libraryFolders"
                :key="folder"
                class="flex items-center gap-2 border rounded-lg bg-white p-3 dark:bg-gray-900"
              >
                <span class="i-mdi:folder inline-block h-5 w-5 text-primary" />
                <span class="text-gray-600 font-medium dark:text-gray-400">库 {{ idx + 1 }}:</span>
                <span class="flex-1 text-sm font-mono">{{ folder }}</span>
              </div>
            </div>
          </div>
          <div v-else-if="testResults.libraryFolders" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-empty description="暂无库目录数据" />
          </div>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped lang="scss">
pre {
  padding: 12px;
  overflow-x: auto;
  background: var(--el-bg-color);
  border-radius: 4px;
}
</style>
