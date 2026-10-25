import { ActionTooltip, type DesktopPluginHost } from '@dotcraft/plugin'
import { useEffect, useRef, useState, useSyncExternalStore, type JSX } from 'react'
import { formatDuration, stringsFor, type Strings } from './i18n'
import type { ClaudeUsage, UsageFeed, UsageWindow } from './usage'

export function ClaudeMark({ size = 14 }: { size?: number }): JSX.Element {
  return (
    <svg width={size} height={size} viewBox="8 14 48 36" aria-hidden="true" className="claude-code-mark">
      <path fill="#D77757" d="M14 18h36v12h6v6h-6v6H14v-6H8v-6h6ZM17 42h3v6h-3Zm6 0h3v6h-3Zm15 0h3v6h-3Zm6 0h3v6h-3Z" />
      <path fill="#000" d="M20 24h3v6h-3Zm21 0h3v6h-3Z" />
    </svg>
  )
}

export function useUsage(feed: UsageFeed, providerId: string): ClaudeUsage | null {
  const [, setRevision] = useState(0)
  useEffect(() => feed.watch(providerId, () => setRevision((value) => value + 1)), [feed, providerId])
  return feed.get(providerId)
}

export function useNow(intervalMs: number): number {
  return useSyncExternalStore(
    (notify) => {
      const id = setInterval(notify, intervalMs)
      return () => clearInterval(id)
    },
    () => Math.floor(Date.now() / intervalMs) * intervalMs
  )
}

export function limitMessage(usage: ClaudeUsage, now: number, strings: Strings): string | null {
  const window = usage.limitReachedKind === 'seven_day' ? usage.weekly : usage.session
  const resets = window ? resetIn(window, now, strings) : null
  if (usage.limitReachedKind === 'extra_usage') return strings.extraUsageBlocked(resets)
  if (usage.limitReachedKind === 'five_hour') return strings.limitReached(strings.session, resets)
  if (usage.limitReachedKind === 'seven_day') return strings.limitReached(strings.weekly, resets)
  return usage.limitReachedKind ? strings.limitReached(strings.session, null) : null
}

function resetIn(window: UsageWindow, now: number, strings: Strings): string {
  return formatDuration(Math.max(0, Date.parse(window.resetAt) - now), strings)
}

function remaining(window: UsageWindow | null): number | null {
  return window ? Math.max(0, Math.min(100, 100 - Math.round(window.usedPercent))) : null
}

export function Subscription({ host, feed, providerId }: { host: DesktopPluginHost; feed: UsageFeed; providerId: string }): JSX.Element | null {
  const usage = useUsage(feed, providerId)
  const now = useNow(30_000)
  const strings = stringsFor(host.environment.locale)
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (!open) return
    const close = (event: Event): void => {
      if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.contains(event.target as Node)) setOpen(false)
    }
    window.addEventListener('pointerdown', close, true)
    window.addEventListener('keydown', close)
    return () => {
      window.removeEventListener('pointerdown', close, true)
      window.removeEventListener('keydown', close)
    }
  }, [open])

  if (!usage) return null
  const left = Math.min(remaining(usage.session) ?? 100, remaining(usage.weekly) ?? 100)
  const blocked = usage.limitReachedKind !== null
  const tone = blocked ? 'error' : toneFor(left)
  const rows: Array<[string, UsageWindow]> = []
  if (usage.session) rows.push([strings.session, usage.session])
  if (usage.weekly) rows.push([strings.weekly, usage.weekly])
  const label = [strings.title, ...rows.map(([name, window]) => `${name} ${strings.left(remaining(window) ?? 0)}`)].join(' – ')
  const message = limitMessage(usage, now, strings)
  const tooltip = [
    usage.session ? strings.sessionShort(remaining(usage.session) ?? 0) : null,
    usage.weekly ? strings.weeklyShort(remaining(usage.weekly) ?? 0) : null
  ].filter(Boolean).join(', ') || strings.unavailable

  return (
    <div ref={root} className="claude-code-usage">
      <ActionTooltip label={open ? '' : tooltip}>
        <button
          type="button"
          className="dc-composer-pill claude-code-usage-pill"
          data-tone={tone}
          aria-label={label}
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
        >
          <ClaudeMark />
          <span className="claude-code-usage-track" aria-hidden="true">
            <span className="claude-code-usage-fill" style={{ width: `${blocked ? 0 : left}%` }} />
          </span>
        </button>
      </ActionTooltip>
      {open ? (
        <div className="claude-code-usage-popover" role="dialog" aria-label={strings.title}>
          <div className="claude-code-usage-header">
            <span className="claude-code-usage-title">
              {strings.title}
              {usage.planType ? <span className="claude-code-usage-plan"> {planName(usage.planType)}</span> : null}
            </span>
            {usage.fetchedAt ? (
              <span className="claude-code-usage-dim">
                {strings.fetched(new Date(usage.fetchedAt).toLocaleTimeString(host.environment.locale, { hour: '2-digit', minute: '2-digit' }))}
              </span>
            ) : null}
          </div>
          <div className="claude-code-usage-divider" />
          {rows.length === 0 && !message ? <div className="claude-code-usage-dim">{strings.unavailable}</div> : null}
          {rows.map(([name, window]) => (
            <div key={name} className="claude-code-usage-row">
              <span className="claude-code-usage-label">{name}</span>
              <span className="claude-code-usage-bar" data-tone={toneFor(remaining(window) ?? 0)} aria-hidden="true">
                <span className="claude-code-usage-fill" style={{ width: `${remaining(window) ?? 0}%` }} />
              </span>
              <div className="claude-code-usage-row-foot" data-tone={toneFor(remaining(window) ?? 0)}>
                <span className="claude-code-usage-percent">{strings.left(remaining(window) ?? 0)}</span>
                <span>{strings.resetsIn(resetIn(window, now, strings))}</span>
              </div>
            </div>
          ))}
          {message ? <div className="claude-code-usage-alert" role="alert">{message}</div> : null}
        </div>
      ) : null}
    </div>
  )
}

function toneFor(left: number): 'normal' | 'warning' | 'error' {
  return left < 20 ? 'error' : left < 40 ? 'warning' : 'normal'
}

function planName(plan: string): string {
  return plan.charAt(0).toUpperCase() + plan.slice(1)
}
