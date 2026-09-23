export default defineNuxtConfig({
  ssr: false,
  compatibilityDate: '2026-09-24',
  devtools: { enabled: false },
  modules: ['@pinia/nuxt', '@nuxtjs/tailwindcss'],
  pinia: { storesDirs: ['./stores/**'] },   // 讓 useSessionStore 可以 auto-import
  css: ['~/assets/css/main.css'],
  app: {
    head: {
      title: 'Prompt Copilot',
      htmlAttrs: { lang: 'zh-Hant' },
      meta: [{ name: 'viewport', content: 'width=device-width, initial-scale=1' }],
    },
  },
  runtimeConfig: {
    public: {
      // '' = 同源（經 devProxy／nginx 反代）。devProxy 若會緩衝 SSE，改成 http://localhost:5000 並在 API 的 Development 開 CORS（spec §2.5 備案）。
      apiBase: '',
    },
  },
  nitro: {
    devProxy: {
      '/api': { target: 'http://localhost:5000/api', changeOrigin: true },
      '/health': { target: 'http://localhost:5000/health', changeOrigin: true },
    },
  },
})
