export interface AchievementOverviewViewModel {
  dto: SteamAchievementOverviewItem
  displayName: string
  hasAchievements: boolean
}

export interface AchievementViewModel {
  dto: SteamAchievement
  displayName: string
  description: string
  iconUrl: string | null
  unlockDate: Date | null
}

export function toOverviewViewModels(
  items: readonly SteamAchievementOverviewItem[],
): AchievementOverviewViewModel[] {
  return items
    .map(dto => ({
      dto,
      displayName: dto.localizedName || dto.name || `App ${dto.appId}`,
      hasAchievements: (dto.progress?.total ?? 0) > 0,
    }))
    .sort(compareOverviewViewModels)
}

export function toAchievementViewModels(
  items: readonly SteamAchievement[],
): AchievementViewModel[] {
  return items
    .filter(dto => dto.isRevealed !== false)
    .map(dto => ({
      dto,
      displayName: dto.localizedName || dto.internalName,
      description: dto.localizedDescription,
      iconUrl: normalizeSteamImageUrl(dto.isUnlocked === true ? dto.icon : dto.iconGray || dto.icon),
      unlockDate: toUnlockDate(dto.unlockTimeUtc),
    }))
    .sort(compareAchievementViewModels)
}

export function normalizeSteamImageUrl(value: string): string | null {
  if (!value) {
    return null
  }
  let url: URL
  try {
    url = new URL(value)
  }
  catch {
    return null
  }
  if (url.protocol !== 'https:' || url.username !== '' || url.password !== '' || url.port !== '') {
    return null
  }
  const host = url.hostname
  if (host !== 'steamcdn-a.akamaihd.net' && !host.endsWith('.steamstatic.com')) {
    return null
  }
  return url.toString()
}

function compareOverviewViewModels(
  a: AchievementOverviewViewModel,
  b: AchievementOverviewViewModel,
): number {
  if (a.hasAchievements !== b.hasAchievements) {
    return a.hasAchievements ? -1 : 1
  }
  const percentageDelta = overviewPercentage(b) - overviewPercentage(a)
  if (percentageDelta !== 0) {
    return percentageDelta
  }
  if (a.dto.lastPlayedAt !== b.dto.lastPlayedAt) {
    return b.dto.lastPlayedAt - a.dto.lastPlayedAt
  }
  return a.dto.appId - b.dto.appId
}

function overviewPercentage(model: AchievementOverviewViewModel): number {
  return model.hasAchievements ? model.dto.progress?.percentage ?? -1 : -1
}

function compareAchievementViewModels(a: AchievementViewModel, b: AchievementViewModel): number {
  const unlockedDelta = unlockedRank(a.dto.isUnlocked) - unlockedRank(b.dto.isUnlocked)
  if (unlockedDelta !== 0) {
    return unlockedDelta
  }
  const nameDelta = a.displayName.localeCompare(b.displayName)
  if (nameDelta !== 0) {
    return nameDelta
  }
  return compareOrdinal(a.dto.internalName, b.dto.internalName)
}

function unlockedRank(isUnlocked?: boolean | null): number {
  if (isUnlocked === true) {
    return 0
  }
  if (isUnlocked === false) {
    return 1
  }
  return 2
}

function compareOrdinal(a: string, b: string): number {
  if (a < b) {
    return -1
  }
  if (a > b) {
    return 1
  }
  return 0
}

function toUnlockDate(unlockTimeUtc?: number | null): Date | null {
  return typeof unlockTimeUtc === 'number' && Number.isFinite(unlockTimeUtc) && unlockTimeUtc > 0
    ? new Date(unlockTimeUtc * 1000)
    : null
}
