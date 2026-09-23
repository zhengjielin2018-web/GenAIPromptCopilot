import type { Config } from 'tailwindcss'

const token = (name: string) => `rgb(var(--${name}) / <alpha-value>)`

export default <Partial<Config>>{
  content: ['./components/**/*.vue', './app.vue', './lib/**/*.ts'],
  theme: {
    extend: {
      colors: {
        paper: token('paper'),
        surface: token('surface'),
        ink: token('ink'),
        muted: token('muted'),
        rule: token('rule'),
        cyan: token('cyan'),
        magenta: token('magenta'),
        yellow: token('yellow'),
        'cyan-wash': token('cyan-wash'),
        'magenta-wash': token('magenta-wash'),
        'yellow-wash': token('yellow-wash'),
      },
      fontFamily: {
        sans: ['"Noto Sans TC"', 'system-ui', '"PingFang TC"', '"Microsoft JhengHei"', 'sans-serif'],
        mono: ['"IBM Plex Mono"', 'ui-monospace', 'Consolas', 'monospace'],
      },
    },
  },
}
