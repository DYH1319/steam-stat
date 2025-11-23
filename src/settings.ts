import type { RecursiveRequired, Settings } from '#/global'
import { cloneDeep } from 'es-toolkit'
import settingsDefault from '@/settings.default'
import { merge } from '@/utils/object'

const globalSettings: Settings.all = {
  home: {
    enable: false,
  },
  menu: {
    mode: 'single',
    mainMenuClickMode: 'smart',
    subMenuUniqueOpened: false,
    enableSubMenuCollapseButton: true,
  },
  toolbar: {
    enable: false,
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
