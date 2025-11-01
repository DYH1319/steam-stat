import type { RecursiveRequired, Settings } from '#/global'
import { cloneDeep } from 'es-toolkit'
import settingsDefault from '@/settings.default'
import { merge } from '@/utils/object'

const globalSettings: Settings.all = {
  app: {
    enablePermission: true,
    enableDynamicTitle: true,
  },
  home: {
    enable: false,
  },
  menu: {
    mode: 'head',
    mainMenuClickMode: 'jump',
    enableSubMenuCollapseButton: true,
  },
  toolbar: {
    fullscreen: true,
    pageReload: true,
  },
  mainPage: {
    enableHotkeys: false,
  },
  navSearch: {
    enableHotkeys: false,
  },
  copyright: {
    enable: true,
    dates: '2025',
    company: 'DYH1319',
    website: 'https://dyh1319.asia',
  },
}

export default merge(globalSettings, cloneDeep(settingsDefault)) as RecursiveRequired<Settings.all>
