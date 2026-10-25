export const LOCALES = ['en', 'zh-Hans', 'ja', 'ko', 'es', 'fr', 'de'] as const

type Locale = (typeof LOCALES)[number]

export interface Strings {
  readonly title: string
  readonly session: string
  readonly weekly: string
  readonly left: (percent: number) => string
  readonly sessionShort: (percent: number) => string
  readonly weeklyShort: (percent: number) => string
  readonly resetsIn: (duration: string) => string
  readonly fetched: (time: string) => string
  readonly unavailable: string
  readonly limitReached: (window: string, duration: string | null) => string
  readonly extraUsageBlocked: (duration: string | null) => string
  readonly days: (days: number, hours: number) => string
  readonly hours: (hours: number, minutes: number) => string
  readonly minutes: (minutes: number) => string
  readonly soon: string
}

const CATALOG: Record<Locale, Strings> = {
  en: {
    title: 'Claude usage',
    session: '5-hour limit',
    weekly: 'Weekly limit',
    left: (percent) => `${percent}% left`,
    sessionShort: (percent) => `${percent}% 5h`,
    weeklyShort: (percent) => `${percent}% wk`,
    resetsIn: (duration) => `Resets in ${duration}`,
    fetched: (time) => `Updated ${time}`,
    unavailable: 'Usage is not available. Check that Claude Code is signed in.',
    limitReached: (window, duration) => `Your Claude ${window.toLowerCase()} is used up.${duration ? ` It resets in ${duration}.` : ''}`,
    extraUsageBlocked: (duration) => `Your Claude limit is used up. Requests are paused because extra usage is off for this provider.${duration ? ` The limit resets in ${duration}.` : ''}`,
    days: (days, hours) => `${days}d ${hours}h`,
    hours: (hours, minutes) => `${hours}h ${minutes}m`,
    minutes: (minutes) => `${minutes}m`,
    soon: 'less than a minute'
  },
  'zh-Hans': {
    title: 'Claude 用量',
    session: '5 小时限额',
    weekly: '每周限额',
    left: (percent) => `剩余 ${percent}%`,
    sessionShort: (percent) => `${percent}% 5h`,
    weeklyShort: (percent) => `${percent}% 周`,
    resetsIn: (duration) => `${duration}后重置`,
    fetched: (time) => `更新于 ${time}`,
    unavailable: '暂时无法获取用量，请确认 Claude Code 已登录。',
    limitReached: (window, duration) => `Claude ${window}已用完。${duration ? `${duration}后重置。` : ''}`,
    extraUsageBlocked: (duration) => `Claude 限额已用完。此 provider 未开启额外用量，请求已暂停。${duration ? `限额${duration}后重置。` : ''}`,
    days: (days, hours) => `${days} 天 ${hours} 小时`,
    hours: (hours, minutes) => `${hours} 小时 ${minutes} 分钟`,
    minutes: (minutes) => `${minutes} 分钟`,
    soon: '不到 1 分钟'
  },
  ja: {
    title: 'Claude の使用量',
    session: '5 時間の上限',
    weekly: '週間の上限',
    left: (percent) => `残り ${percent}%`,
    sessionShort: (percent) => `${percent}% 5 時間`,
    weeklyShort: (percent) => `${percent}% 週`,
    resetsIn: (duration) => `${duration}後にリセット`,
    fetched: (time) => `${time}に更新`,
    unavailable: '使用量を取得できません。Claude Code にサインインしているか確認してください。',
    limitReached: (window, duration) => `Claude の${window}に達しました。${duration ? `${duration}後にリセットされます。` : ''}`,
    extraUsageBlocked: (duration) => `Claude の上限に達しました。このプロバイダーでは追加使用量がオフのため、リクエストを停止しています。${duration ? `上限は${duration}後にリセットされます。` : ''}`,
    days: (days, hours) => `${days}日${hours}時間`,
    hours: (hours, minutes) => `${hours}時間${minutes}分`,
    minutes: (minutes) => `${minutes}分`,
    soon: '1 分未満'
  },
  ko: {
    title: 'Claude 사용량',
    session: '5시간 한도',
    weekly: '주간 한도',
    left: (percent) => `${percent}% 남음`,
    sessionShort: (percent) => `${percent}% 5시간`,
    weeklyShort: (percent) => `${percent}%주`,
    resetsIn: (duration) => `${duration} 후 초기화`,
    fetched: (time) => `${time} 업데이트`,
    unavailable: '사용량을 가져올 수 없습니다. Claude Code에 로그인되어 있는지 확인하세요.',
    limitReached: (window, duration) => `Claude ${window}를 모두 사용했습니다.${duration ? ` ${duration} 후 초기화됩니다.` : ''}`,
    extraUsageBlocked: (duration) => `Claude 한도를 모두 사용했습니다. 이 공급자는 추가 사용량이 꺼져 있어 요청을 일시 중지했습니다.${duration ? ` 한도는 ${duration} 후 초기화됩니다.` : ''}`,
    days: (days, hours) => `${days}일 ${hours}시간`,
    hours: (hours, minutes) => `${hours}시간 ${minutes}분`,
    minutes: (minutes) => `${minutes}분`,
    soon: '1분 미만'
  },
  es: {
    title: 'Uso de Claude',
    session: 'Límite de 5 horas',
    weekly: 'Límite semanal',
    left: (percent) => `Queda ${percent}%`,
    sessionShort: (percent) => `${percent}% 5h`,
    weeklyShort: (percent) => `${percent}% semana`,
    resetsIn: (duration) => `Se restablece en ${duration}`,
    fetched: (time) => `Actualizado ${time}`,
    unavailable: 'El uso no está disponible. Comprueba que Claude Code tiene la sesión iniciada.',
    limitReached: (window, duration) => `Has agotado tu ${window.toLowerCase()} de Claude.${duration ? ` Se restablece en ${duration}.` : ''}`,
    extraUsageBlocked: (duration) => `Has agotado tu límite de Claude. Las solicitudes están en pausa porque el uso adicional está desactivado para este proveedor.${duration ? ` El límite se restablece en ${duration}.` : ''}`,
    days: (days, hours) => `${days} d ${hours} h`,
    hours: (hours, minutes) => `${hours} h ${minutes} min`,
    minutes: (minutes) => `${minutes} min`,
    soon: 'menos de un minuto'
  },
  fr: {
    title: 'Utilisation de Claude',
    session: 'Limite de 5 heures',
    weekly: 'Limite hebdomadaire',
    left: (percent) => `${percent} % restants`,
    sessionShort: (percent) => `${percent}% 5h`,
    weeklyShort: (percent) => `${percent}% semaine`,
    resetsIn: (duration) => `Réinitialisation dans ${duration}`,
    fetched: (time) => `Mis à jour ${time}`,
    unavailable: 'L’utilisation n’est pas disponible. Vérifiez que Claude Code est connecté.',
    limitReached: (window, duration) => `Votre ${window.toLowerCase()} Claude est épuisée.${duration ? ` Elle se réinitialise dans ${duration}.` : ''}`,
    extraUsageBlocked: (duration) => `Votre limite Claude est épuisée. Les requêtes sont suspendues, car l’utilisation supplémentaire est désactivée pour ce fournisseur.${duration ? ` La limite se réinitialise dans ${duration}.` : ''}`,
    days: (days, hours) => `${days} j ${hours} h`,
    hours: (hours, minutes) => `${hours} h ${minutes} min`,
    minutes: (minutes) => `${minutes} min`,
    soon: 'moins d’une minute'
  },
  de: {
    title: 'Claude-Nutzung',
    session: '5-Stunden-Limit',
    weekly: 'Wochenlimit',
    left: (percent) => `${percent} % übrig`,
    sessionShort: (percent) => `${percent}% 5h`,
    weeklyShort: (percent) => `${percent}% Wo`,
    resetsIn: (duration) => `Zurückgesetzt in ${duration}`,
    fetched: (time) => `Aktualisiert ${time}`,
    unavailable: 'Die Nutzung ist nicht verfügbar. Prüfe, ob Claude Code angemeldet ist.',
    limitReached: (window, duration) => `Dein Claude-${window} ist aufgebraucht.${duration ? ` Es wird in ${duration} zurückgesetzt.` : ''}`,
    extraUsageBlocked: (duration) => `Dein Claude-Limit ist aufgebraucht. Anfragen sind pausiert, weil zusätzliche Nutzung für diesen Anbieter ausgeschaltet ist.${duration ? ` Das Limit wird in ${duration} zurückgesetzt.` : ''}`,
    days: (days, hours) => `${days} T ${hours} Std.`,
    hours: (hours, minutes) => `${hours} Std. ${minutes} Min.`,
    minutes: (minutes) => `${minutes} Min.`,
    soon: 'weniger als einer Minute'
  }
}

export function stringsFor(locale: string): Strings {
  const exact = CATALOG[locale as Locale]
  if (exact !== undefined) return exact
  const base = locale.split('-')[0]
  const match = LOCALES.find((candidate) => candidate.split('-')[0] === base)
  return match !== undefined ? CATALOG[match] : CATALOG.en
}

export function formatDuration(ms: number, strings: Strings): string {
  const minutes = Math.floor(ms / 60_000)
  if (minutes < 1) return strings.soon
  const days = Math.floor(minutes / 1_440)
  const hours = Math.floor((minutes % 1_440) / 60)
  if (days > 0) return strings.days(days, hours)
  if (hours > 0) return strings.hours(hours, minutes % 60)
  return strings.minutes(minutes)
}
