import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import type { ComponentType, ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { activate } from './index'

vi.mock('@dotcraft/plugin', () => ({ ActionTooltip: ({ children }: { children: ReactNode }) => children }))

type Surface = ComponentType<{ context: Record<string, unknown>; children?: ReactNode }>

function setup(usage: Record<string, unknown>) {
  const surfaces = new Map<string, Surface>()
  const notifications = new Map<string, () => void>()
  let current = usage
  const request = vi.fn(async () => current)
  const host = {
    environment: { locale: 'en' },
    effect: (setup: () => unknown) => setup(),
    appServer: {
      request,
      onNotification: (method: string, listener: () => void) => {
        notifications.set(method, listener)
        return () => notifications.delete(method)
      }
    },
    ui: {
      wrap: (name: string, component: Surface) => surfaces.set(name, component),
      add: (name: string, component: Surface) => surfaces.set(name, component)
    }
  }
  void activate(host as never)
  return {
    request,
    surface: (name: string) => surfaces.get(name)!,
    notify: (method: string) => notifications.get(method)?.(),
    update: (next: Record<string, unknown>) => { current = next }
  }
}

const context = (backendId: string | null) => ({ minimalChrome: false, provider: { id: 'subscription', backendId } })
const window5h = (usedPercent: number) => ({ usedPercent, windowSeconds: 18_000, resetAt: '2099-01-01T05:00:00.000Z' })

describe('Claude Code desktop module', () => {
  afterEach(cleanup)

  it('replaces the usage indicator only for its own backend', async () => {
    const plugin = setup({ available: true, primary: window5h(20) })
    const Usage = plugin.surface('composer.status.subscription')

    render(<Usage context={context('other')}><span data-testid="core-usage" /></Usage>)
    expect(screen.getByTestId('core-usage')).toBeTruthy()
    expect(plugin.request).not.toHaveBeenCalled()
    cleanup()

    render(<Usage context={context('claude-code')}><span data-testid="core-usage" /></Usage>)
    expect(screen.queryByTestId('core-usage')).toBeNull()
    await waitFor(() => expect(plugin.request).toHaveBeenCalledWith('provider/auth/usage', { providerId: 'subscription' }, 30_000))
    expect(await screen.findByRole('button', { name: /80% left/ })).toBeTruthy()
  })

  it('reads usage again after a turn and shows a notice once a limit is used up', async () => {
    const plugin = setup({ available: true, primary: window5h(90) })
    const Usage = plugin.surface('composer.status.subscription')
    const Notice = plugin.surface('composer.before')
    render(<><Notice context={context('claude-code')} /><Usage context={context('claude-code')} /></>)
    expect(await screen.findByRole('button', { name: /10% left/ })).toBeTruthy()
    expect(screen.queryByRole('status')).toBeNull()

    plugin.update({ available: true, primary: window5h(100), limitReachedKind: 'extra_usage' })
    await act(async () => plugin.notify('turn/completed'))

    expect(await screen.findByRole('status')).toBeTruthy()
    expect(plugin.request).toHaveBeenCalledTimes(2)
  })
})
