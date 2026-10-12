import apiApp from '@/api/modules/app'
import localSvg from '@/assets/icons/i-mdi-local.svg'
import router from '@/router'

export const useAppAccountStore = defineStore('appAccount', () => {
  const appSettingsStore = useAppSettingsStore()
  const appTabbarStore = useAppTabbarStore()
  const appRouteStore = useAppRouteStore()
  const appMenuStore = useAppMenuStore()

  // 账号信息
  const token = ref(localStorage.getItem('token') ?? '')
  const account = ref(localStorage.getItem('account') ?? 'Steam 本地数据')
  const avatar = ref(localStorage.getItem('avatar') ?? localSvg)

  // 权限信息
  const permissions = ref<string[]>([])

  // 登录状态（桌面应用视为始终登录的本机用户）
  const isLogin = computed(() => true)

  // 登录
  async function login(data: {
    account: string
    password: string
  }) {
    const res = await apiApp.login(data)
    localStorage.setItem('account', res.data.account)
    localStorage.setItem('token', res.data.token)
    localStorage.setItem('avatar', res.data.avatar)
    account.value = res.data.account
    token.value = res.data.token
    avatar.value = res.data.avatar
  }

  // 手动登出
  function logout(redirect = router.currentRoute.value.fullPath) {
    localStorage.removeItem('token')
    token.value = ''
    router.push({
      name: 'login',
      query: {
        ...(redirect !== appSettingsStore.settings.app.home.fullPath && router.currentRoute.value.name !== 'login' && { redirect }),
      },
    }).then(logoutCleanStatus)
  }

  // 请求登出
  function requestLogout() {
    localStorage.removeItem('token')
    token.value = ''
    router.push({
      name: 'login',
      query: {
        ...(
          router.currentRoute.value.fullPath !== appSettingsStore.settings.app.home.fullPath
          && router.currentRoute.value.name !== 'login'
          && {
            redirect: router.currentRoute.value.fullPath,
          }
        ),
      },
    }).then(logoutCleanStatus)
  }

  // 登出后清除状态
  function logoutCleanStatus() {
    localStorage.removeItem('account')
    localStorage.removeItem('avatar')
    account.value = ''
    avatar.value = ''
    permissions.value = []
    appSettingsStore.updateSettings({}, true)
    appTabbarStore.clean()
    appRouteStore.removeRoutes()
    appMenuStore.setActived(0)
  }

  // 获取权限
  async function getPermissions() {
    const res = await apiApp.permission()
    permissions.value = res.data.permissions
  }

  // 修改密码
  async function editPassword(data: {
    password: string
    newPassword: string
  }) {
    await apiApp.passwordEdit(data)
  }

  return {
    token,
    account,
    avatar,
    permissions,
    isLogin,
    login,
    logout,
    requestLogout,
    getPermissions,
    editPassword,
  }
})
