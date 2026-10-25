import type { DesktopPluginActivate } from '@dotcraft/plugin'
import { LimitNotice } from './LimitNotice'
import { Subscription } from './Subscription'
import { claudeProviderId, startUsageFeed } from './usage'
import './styles/index.css'

export const activate: DesktopPluginActivate = (host) => {
  const feed = startUsageFeed(host)
  host.effect(() => feed.dispose)

  host.ui.wrap('composer.status.subscription', ({ context, children }) => {
    const providerId = claudeProviderId(context)
    return providerId ? <Subscription host={host} feed={feed} providerId={providerId} /> : <>{children}</>
  })
  host.ui.add('composer.before', ({ context }) => {
    const providerId = claudeProviderId(context)
    return providerId && !context.minimalChrome ? <LimitNotice host={host} feed={feed} providerId={providerId} /> : null
  })
}
