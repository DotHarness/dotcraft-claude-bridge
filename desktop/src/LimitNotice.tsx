import type { DesktopPluginHost } from '@dotcraft/plugin'
import type { JSX } from 'react'
import { stringsFor } from './i18n'
import { ClaudeMark, limitMessage, useNow, useUsage } from './Subscription'
import type { UsageFeed } from './usage'

export function LimitNotice({ host, feed, providerId }: { host: DesktopPluginHost; feed: UsageFeed; providerId: string }): JSX.Element | null {
  const usage = useUsage(feed, providerId)
  const now = useNow(30_000)
  const message = usage ? limitMessage(usage, now, stringsFor(host.environment.locale)) : null
  if (!message) return null
  return (
    <div className="claude-code-limit-notice" role="status">
      <ClaudeMark />
      <span>{message}</span>
    </div>
  )
}
