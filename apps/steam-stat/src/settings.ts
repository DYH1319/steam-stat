import { setSettings } from '@fantastic-admin/settings'

export default setSettings({
  app: {
    home: {
      enable: false,
    },
    copyright: {
      enable: true,
      dates: '2025-2026',
      company: 'DYH1319',
      website: 'https://dyh1319.asia',
    },
  },
  theme: {
    radius: 0.5,
  },
  menu: {
    mode: 'single',
    mainMenuClickMode: 'smart',
    subMenuUniqueExpand: false,
    subMenuCollapseButton: true,
    hotkeys: false,
  },
  topbar: {
    toolbar: false,
    tabbar: false,
  },
  toolbar: {
    breadcrumb: false,
    menuSearch: {
      enable: false,
      hotkeys: false,
    },
    fullscreen: true,
    pageReload: true,
    colorScheme: false,
  },
})
