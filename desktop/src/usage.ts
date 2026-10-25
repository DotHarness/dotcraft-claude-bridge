import type { DesktopPluginComposerSurfaceContext, DesktopPluginHost } from '@dotcraft/plugin'

export const BACKEND_ID = 'claude-code'
const REFRESH_INTERVAL_MS = 5 * 60_000

export interface UsageWindow {
  readonly usedPercent: number
  readonly resetAt: string
}

export interface ClaudeUsage {
  readonly planType: string | null
  readonly session: UsageWindow | null
  readonly weekly: UsageWindow | null
  readonly limitReachedKind: string | null
  readonly fetchedAt: string | null
}

export function claudeProviderId({ provider }: DesktopPluginComposerSurfaceContext): string | null {
  return provider?.backendId === BACKEND_ID ? provider.id : null
}

export interface UsageFeed {
  get(providerId: string): ClaudeUsage | null
  watch(providerId: string, listener: () => void): () => void
}

export function startUsageFeed(host: DesktopPluginHost): UsageFeed & { dispose(): void } {
  const usage = new Map<string, ClaudeUsage | null>()
  const listeners = new Map<string, Set<() => void>>()
  const pending = new Set<string>()

  async function refresh(providerId: string): Promise<void> {
    if (pending.has(providerId)) return
    pending.add(providerId)
    try {
      const result = await host.appServer.request('provider/auth/usage', { providerId }, 30_000)
      usage.set(providerId, result.available ? {
        planType: result.planType ?? null,
        session: windowOf(result.primary),
        weekly: windowOf(result.secondary),
        limitReachedKind: result.limitReachedKind ?? null,
        fetchedAt: result.fetchedAt ?? null
      } : null)
      for (const listener of listeners.get(providerId) ?? []) listener()
    } catch {
      return
    } finally {
      pending.delete(providerId)
    }
  }

  const refreshWatched = (): void => {
    for (const providerId of listeners.keys()) void refresh(providerId)
  }
  const stops = [
    host.appServer.onNotification('turn/completed', refreshWatched),
    host.appServer.onNotification('turn/failed', refreshWatched)
  ]
  const timer = setInterval(refreshWatched, REFRESH_INTERVAL_MS)

  return {
    get: (providerId) => usage.get(providerId) ?? null,
    watch(providerId, listener) {
      const set = listeners.get(providerId) ?? new Set()
      listeners.set(providerId, set)
      set.add(listener)
      if (set.size === 1) void refresh(providerId)
      return () => {
        set.delete(listener)
        if (set.size === 0) listeners.delete(providerId)
      }
    },
    dispose() {
      clearInterval(timer)
      for (const stop of stops) stop()
      listeners.clear()
    }
  }
}

function windowOf(value: { usedPercent: number; resetAt: string } | null | undefined): UsageWindow | null {
  return value ? { usedPercent: value.usedPercent, resetAt: value.resetAt } : null
}
