<template>
  <transition name="drawer">
    <aside v-if="s.drawerPresetId !== null" role="dialog" aria-label="preset 詳情"
           class="absolute inset-y-0 right-0 z-20 w-full max-w-md overflow-y-auto border-l border-neutral-200 bg-white p-4 shadow-xl dark:border-neutral-800 dark:bg-neutral-900">
      <div class="flex items-center justify-between">
        <h2 class="text-sm font-semibold">Preset #{{ s.drawerPresetId }}</h2>
        <button type="button" class="text-sm text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100" @click="s.closeDrawer()">關閉 ✕</button>
      </div>
      <p v-if="loading" class="mt-4 text-xs text-neutral-500">載入中…</p>
      <p v-else-if="error" class="mt-4 text-xs text-red-600">{{ error }}</p>
      <template v-else-if="preset">
        <img v-if="preset.imageUrl && !imgFailed" :src="preset.imageUrl" :alt="preset.title" referrerpolicy="no-referrer"
             class="mt-3 w-full rounded object-cover" @error="imgFailed = true">
        <div v-else class="mt-3 flex h-40 items-center justify-center rounded bg-neutral-100 text-xs text-neutral-400 dark:bg-neutral-800">沒有可顯示的圖片</div>
        <h3 class="mt-3 text-base font-semibold">{{ preset.title }}</h3>
        <p class="text-xs text-neutral-500">{{ preset.category }}</p>
        <p class="mt-2 text-sm">{{ preset.description }}</p>
        <PromptBlock label="Prompt snippet" :text="preset.promptSnippet" />
        <PromptBlock v-if="preset.negativeSnippet" label="Negative snippet" :text="preset.negativeSnippet" />
        <section class="mt-3">
          <h4 class="text-xs font-medium text-neutral-500">Tags</h4>
          <div class="mt-1 flex flex-wrap gap-1">
            <span v-for="t in preset.tags" :key="t" class="rounded bg-neutral-100 px-1.5 py-0.5 font-mono text-[11px] dark:bg-neutral-800">{{ t }}</span>
          </div>
        </section>
        <section class="mt-3">
          <h4 class="text-xs font-medium text-neutral-500">Facets</h4>
          <div class="mt-1 flex flex-wrap gap-1">
            <span v-for="f in preset.facetIds" :key="f" class="rounded-full border border-neutral-300 px-2 py-0.5 text-[11px] dark:border-neutral-700">{{ f }}</span>
          </div>
        </section>
        <p class="mt-4 text-[10px] text-neutral-400">圖片來自來源網站，本服務不轉存。</p>
      </template>
    </aside>
  </transition>
</template>

<script setup lang="ts">
import type { PresetDetail } from '../types/api'
const s = useSessionStore()
const api = useApi()
const preset = ref<PresetDetail | null>(null)
const loading = ref(false)
const error = ref<string | null>(null)
const imgFailed = ref(false)

watch(() => s.drawerPresetId, async (id) => {
  preset.value = null; error.value = null; imgFailed.value = false
  if (id === null) return
  loading.value = true
  try {
    const p = await api.getPreset(id)
    if (s.drawerPresetId !== id) return          // 使用者已經點了別的
    if (p) preset.value = p; else error.value = '找不到這筆 preset。'
  } catch { error.value = '載入失敗，對話不受影響。' }
  finally { loading.value = false }
}, { immediate: true })

function onKey(e: KeyboardEvent) { if (e.key === 'Escape') s.closeDrawer() }
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<style scoped>
.drawer-enter-active, .drawer-leave-active { transition: transform 200ms ease; }
.drawer-enter-from, .drawer-leave-to { transform: translateX(100%); }
</style>
