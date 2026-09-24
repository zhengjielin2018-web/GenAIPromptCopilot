<template>
  <transition name="drawer">
    <aside v-if="s.drawerPresetId !== null" role="dialog" aria-label="preset 詳情"
           class="absolute inset-y-0 right-0 z-20 w-full max-w-md overflow-y-auto border-l border-rule bg-surface px-5 py-4 shadow-[-12px_0_32px_-16px_rgb(var(--ink)/0.25)]">
      <div class="flex items-center justify-between">
        <h2 class="text-xs text-muted">知識庫範例 #{{ s.drawerPresetId }}</h2>
        <button type="button" class="rounded px-1.5 text-sm text-muted hover:text-ink" @click="s.closeDrawer()">關閉</button>
      </div>
      <p v-if="loading" class="mt-4 text-xs text-muted">載入中…</p>
      <p v-else-if="error" class="mt-4 text-xs text-magenta">{{ error }}</p>
      <template v-else-if="preset">
        <img v-if="preset.imageUrl && !imgFailed" :src="preset.imageUrl" :alt="preset.title" referrerpolicy="no-referrer"
             class="mt-3 w-full rounded-[4px] object-cover" @error="imgFailed = true">
        <div v-else class="mt-3 flex h-40 items-center justify-center rounded-[4px] border border-dashed border-rule text-xs text-muted">沒有可顯示的圖片</div>
        <h3 class="mt-4 text-lg font-bold leading-snug">{{ preset.title }}</h3>
        <p class="mt-0.5 text-xs text-muted">{{ preset.category }}</p>
        <p class="mt-2 text-sm leading-6">{{ preset.description }}</p>
        <PromptBlock label="提示詞片段" :text="preset.promptSnippet" />
        <PromptBlock v-if="preset.negativeSnippet" label="負向片段" :text="preset.negativeSnippet" />
        <section class="mt-3">
          <h4 class="text-xs font-bold">Tags</h4>
          <div class="mt-1 flex flex-wrap gap-1">
            <span v-for="t in preset.tags" :key="t" class="rounded-[3px] bg-paper px-1.5 py-0.5 font-mono text-[11px]">{{ t }}</span>
          </div>
        </section>
        <section class="mt-3">
          <h4 class="text-xs font-bold">對應的 facet</h4>
          <div class="mt-1 flex flex-wrap gap-1">
            <span v-for="f in preset.facetIds" :key="f" class="rounded-[3px] border border-rule px-1.5 py-0.5 font-mono text-[11px] text-muted">{{ f }}</span>
          </div>
        </section>
        <p class="mt-5 text-[11px] text-muted">
          <template v-if="preset.sourceUrl && sourceName(preset.sourceRef)">
            出處：<a :href="preset.sourceUrl" target="_blank" rel="noopener noreferrer" class="underline underline-offset-2 hover:text-ink">{{ sourceName(preset.sourceRef) }}</a>。圖片不轉存。
          </template>
          <template v-else>圖片來自來源網站，本服務不轉存。</template>
        </p>
      </template>
    </aside>
  </transition>
</template>

<script setup lang="ts">
import type { PresetDetail } from '../types/api'
import { sourceName } from '../lib/copy'
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
