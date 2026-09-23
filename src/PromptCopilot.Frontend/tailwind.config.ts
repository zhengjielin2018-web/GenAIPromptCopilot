import type { Config } from 'tailwindcss'

export default <Partial<Config>>{
  content: ['./components/**/*.vue', './app.vue', './lib/**/*.ts'],
  theme: { extend: {} },
}
