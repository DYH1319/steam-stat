import { describe, expect, it } from 'vitest'
import {
  normalizeSteamImageUrl,
  toAchievementViewModels,
  toOverviewViewModels,
} from './viewModel'

function overviewItem(overrides: Partial<SteamAchievementOverviewItem>): SteamAchievementOverviewItem {
  return {
    appId: 0,
    lastPlayedAt: 0,
    localizedName: '',
    name: '',
    playtimeForever: 0,
    ...overrides,
  }
}

function achievement(overrides: Partial<SteamAchievement>): SteamAchievement {
  return {
    archived: false,
    hidden: false,
    icon: '',
    iconGray: '',
    internalName: 'ACH',
    isRevealed: true,
    localizedDescription: '',
    localizedName: '',
    progressType: 'none',
    ...overrides,
  }
}

describe('toOverviewViewModels', () => {
  it('falls back through localized name, name and app id', () => {
    const models = toOverviewViewModels([
      overviewItem({ appId: 1, localizedName: '本地名', name: 'English' }),
      overviewItem({ appId: 2, localizedName: '', name: 'English Only' }),
      overviewItem({ appId: 3, localizedName: '', name: '' }),
    ])
    const byId = new Map(models.map(model => [model.dto.appId, model]))
    expect(byId.get(1)?.displayName).toBe('本地名')
    expect(byId.get(2)?.displayName).toBe('English Only')
    expect(byId.get(3)?.displayName).toBe('App 3')
  })

  it('sorts games with achievements first, then percentage, last played and app id', () => {
    const models = toOverviewViewModels([
      overviewItem({ appId: 10, lastPlayedAt: 100 }),
      overviewItem({
        appId: 20,
        lastPlayedAt: 50,
        progress: { appId: 20, percentage: 50, total: 10, unlocked: 5 },
      }),
      overviewItem({
        appId: 30,
        lastPlayedAt: 10,
        progress: { appId: 30, percentage: 80, total: 5, unlocked: 4 },
      }),
      overviewItem({
        appId: 40,
        lastPlayedAt: 90,
        progress: { appId: 40, percentage: 50, total: 0, unlocked: 0 },
      }),
      overviewItem({
        appId: 50,
        lastPlayedAt: 30,
        progress: { appId: 50, percentage: 50, total: 8, unlocked: 4 },
      }),
      overviewItem({ appId: 5, lastPlayedAt: 100 }),
    ])
    expect(models.map(model => model.dto.appId)).toEqual([30, 20, 50, 5, 10, 40])
    expect(models[0]?.hasAchievements).toBe(true)
    expect(models[5]?.hasAchievements).toBe(false)
  })
})

describe('toAchievementViewModels', () => {
  it('filters out achievements that are not revealed', () => {
    const models = toAchievementViewModels([
      achievement({ internalName: 'HIDDEN', isRevealed: false, localizedName: 'secret' }),
      achievement({ internalName: 'SHOWN', isRevealed: true, localizedName: 'shown' }),
    ])
    expect(models.map(model => model.dto.internalName)).toEqual(['SHOWN'])
  })

  it('falls back to the internal name and keeps the description', () => {
    const [model] = toAchievementViewModels([
      achievement({ internalName: 'ACH_1', localizedName: '', localizedDescription: 'desc' }),
    ])
    expect(model?.displayName).toBe('ACH_1')
    expect(model?.description).toBe('desc')
  })

  it('converts positive unix seconds to a local date and ignores invalid values', () => {
    const models = toAchievementViewModels([
      achievement({ internalName: 'A', unlockTimeUtc: 1_700_000_000 }),
      achievement({ internalName: 'B', unlockTimeUtc: 0 }),
      achievement({ internalName: 'C', unlockTimeUtc: -5 }),
      achievement({ internalName: 'D' }),
    ])
    const byName = new Map(models.map(model => [model.dto.internalName, model]))
    expect(byName.get('A')?.unlockDate).toEqual(new Date(1_700_000_000 * 1000))
    expect(byName.get('B')?.unlockDate).toBeNull()
    expect(byName.get('C')?.unlockDate).toBeNull()
    expect(byName.get('D')?.unlockDate).toBeNull()
  })

  it('uses the colored icon for unlocked and the gray icon otherwise', () => {
    const colored = 'https://shared.steamstatic.com/colored.jpg'
    const gray = 'https://shared.steamstatic.com/gray.jpg'
    const models = toAchievementViewModels([
      achievement({ internalName: 'U', isUnlocked: true, icon: colored, iconGray: gray }),
      achievement({ internalName: 'L', isUnlocked: false, icon: colored, iconGray: gray }),
      achievement({ internalName: 'F', isUnlocked: false, icon: colored, iconGray: '' }),
      achievement({ internalName: 'X', isUnlocked: true, icon: 'https://evil.example.com/x.jpg', iconGray: gray }),
    ])
    const byName = new Map(models.map(model => [model.dto.internalName, model]))
    expect(byName.get('U')?.iconUrl).toBe(colored)
    expect(byName.get('L')?.iconUrl).toBe(gray)
    expect(byName.get('F')?.iconUrl).toBe(colored)
    expect(byName.get('X')?.iconUrl).toBeNull()
  })

  it('sorts unlocked, locked and unknown achievements then by name and internal name', () => {
    const models = toAchievementViewModels([
      achievement({ internalName: 'u2', isUnlocked: undefined, localizedName: 'mid' }),
      achievement({ internalName: 'l1', isUnlocked: false, localizedName: 'aaa' }),
      achievement({ internalName: 'u1', isUnlocked: true, localizedName: 'zzz' }),
      achievement({ internalName: 'u0', isUnlocked: true, localizedName: 'aaa' }),
      achievement({ internalName: 'B', isUnlocked: false, localizedName: 'same' }),
      achievement({ internalName: 'b', isUnlocked: false, localizedName: 'same' }),
    ])
    expect(models.map(model => model.dto.internalName)).toEqual(['u0', 'u1', 'l1', 'B', 'b', 'u2'])
  })
})

describe('normalizeSteamImageUrl', () => {
  it('accepts https urls on allowed hosts', () => {
    expect(normalizeSteamImageUrl('https://shared.steamstatic.com/a.jpg')).toBe('https://shared.steamstatic.com/a.jpg')
    expect(normalizeSteamImageUrl('https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/1/a.jpg')).toBe('https://steamcdn-a.akamaihd.net/steamcommunity/public/images/apps/1/a.jpg')
  })

  it('rejects non-https schemes, credentials, ports and other hosts', () => {
    expect(normalizeSteamImageUrl('http://shared.steamstatic.com/a.jpg')).toBeNull()
    expect(normalizeSteamImageUrl('https://user:pass@shared.steamstatic.com/a.jpg')).toBeNull()
    expect(normalizeSteamImageUrl('https://shared.steamstatic.com:8443/a.jpg')).toBeNull()
    expect(normalizeSteamImageUrl('https://evil.example.com/a.jpg')).toBeNull()
    expect(normalizeSteamImageUrl('https://steamstatic.com/a.jpg')).toBeNull()
    expect(normalizeSteamImageUrl('https://steamstatic.com.evil.example/a.jpg')).toBeNull()
    expect(normalizeSteamImageUrl('https://x.steamcdn-a.akamaihd.net/a.jpg')).toBeNull()
  })

  it('rejects invalid and relative urls', () => {
    expect(normalizeSteamImageUrl('')).toBeNull()
    expect(normalizeSteamImageUrl('not a url')).toBeNull()
    expect(normalizeSteamImageUrl('/relative/icon.jpg')).toBeNull()
  })
})
