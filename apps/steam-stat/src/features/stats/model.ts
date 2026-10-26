/** 统计用会话（已应用清洗规则） */
export interface StatSession {
  appId: number
  steamId: string
  start: number
  duration: number
  idle: number
  end: number
}

export interface StatApp {
  appId: number
  name: string
  type?: string | null
  developer?: string | null
  publisher?: string | null
  installed: boolean
}

export interface StatsData {
  sessions: StatSession[]
  apps: Map<number, StatApp>
  users: StatsUser[]
  baselines: StatsBaseline[]
  lastUpdateTime: number
}

export const SESSION_STRIDE = 5

export function emptyStatsData(): StatsData {
  return { sessions: [], apps: new Map(), users: [], baselines: [], lastUpdateTime: 0 }
}

/** 解码分页返回的扁平会话数组（appId, userIndex, start, duration, idle） */
export function decodeDataset(pages: StatsDataset[]): StatsData {
  const head = pages[0]
  if (!head) {
    return emptyStatsData()
  }
  const sessions: StatSession[] = []
  for (const page of pages) {
    const flat = page.sessions
    for (let i = 0; i + SESSION_STRIDE <= flat.length; i += SESSION_STRIDE) {
      const start = flat[i + 2]
      const duration = flat[i + 3]
      sessions.push({
        appId: flat[i],
        steamId: head.users[flat[i + 1]]?.steamId ?? '',
        start,
        duration,
        idle: flat[i + 4],
        end: start + duration,
      })
    }
  }
  sessions.sort((a, b) => a.start - b.start)
  const apps = new Map<number, StatApp>()
  for (const app of head.apps) {
    apps.set(app.appId, {
      appId: app.appId,
      name: app.name || `App ${app.appId}`,
      type: app.type,
      developer: app.developer,
      publisher: app.publisher,
      installed: app.installed,
    })
  }
  return {
    sessions,
    apps,
    users: head.users,
    baselines: head.baselines,
    lastUpdateTime: pages.at(-1)?.lastUpdateTime ?? head.lastUpdateTime,
  }
}

export function appName(apps: Map<number, StatApp>, appId: number): string {
  return apps.get(appId)?.name ?? `App ${appId}`
}

export function steamHeaderImage(appId: number): string {
  return `https://steamcdn-a.akamaihd.net/steam/apps/${appId}/header.jpg`
}

export function steamCapsuleImage(appId: number): string {
  return `https://steamcdn-a.akamaihd.net/steam/apps/${appId}/capsule_184x69.jpg`
}

export function steamHeroImage(appId: number): string {
  return `https://steamcdn-a.akamaihd.net/steam/apps/${appId}/library_hero.jpg`
}
