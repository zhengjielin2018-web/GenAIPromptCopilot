<template>
  <header class="flex items-center justify-between border-b border-rule bg-surface px-5 py-2.5">
    <div class="flex items-center gap-3">
      <!-- 套準標記：印刷對版用的十字圓，這個工具的輸出是「要拿去印（生圖）的稿」 -->
      <svg viewBox="0 0 24 24" class="h-6 w-6 text-ink" aria-hidden="true">
        <circle cx="12" cy="12" r="6.5" fill="none" stroke="currentColor" stroke-width="1.5" />
        <path d="M12 1.5v21M1.5 12h21" stroke="currentColor" stroke-width="1.5" />
        <circle cx="12" cy="12" r="2.2" class="fill-cyan" />
      </svg>
      <div class="leading-tight">
        <h1 class="text-[15px] font-bold tracking-wide">Prompt Copilot</h1>
        <p class="text-[11px] text-muted">把中文描述整理成 SD/SDXL 提示詞</p>
      </div>
    </div>
    <div class="flex items-center gap-4">
      <label class="flex items-center gap-1.5 text-xs text-ink/80">
        <button type="button" role="switch" :aria-checked="s.prefs.retrieval === 'on'"
                class="relative h-4 w-7 rounded-full border transition-colors"
                :class="s.prefs.retrieval === 'on' ? 'border-ink bg-ink' : 'border-muted bg-paper'"
                @click="s.setRetrievalPref(s.prefs.retrieval === 'on' ? 'off' : 'on')">
          <span class="absolute top-0.5 h-2.5 w-2.5 rounded-full transition-[left]"
                :class="s.prefs.retrieval === 'on' ? 'left-[15px] bg-paper' : 'left-0.5 bg-muted'" aria-hidden="true" />
        </button>
        使用知識庫
        <span v-if="s.retrievalMismatch" class="text-[11px] text-muted">新對話後生效</span>
      </label>
      <label class="flex items-center gap-1.5 text-xs text-ink/80">
        <button type="button" role="switch" :aria-checked="s.prefs.showTrace"
                class="relative h-4 w-7 rounded-full border transition-colors"
                :class="s.prefs.showTrace ? 'border-ink bg-ink' : 'border-muted bg-paper'"
                @click="s.setShowTrace(!s.prefs.showTrace)">
          <span class="absolute top-0.5 h-2.5 w-2.5 rounded-full transition-[left]"
                :class="s.prefs.showTrace ? 'left-[15px] bg-paper' : 'left-0.5 bg-muted'" aria-hidden="true" />
        </button>
        顯示檢索細節
      </label>
      <button type="button" :disabled="s.busy || !!s.bootError"
              class="rounded-md border border-ink/80 px-3 py-1 text-sm font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
              @click="onNew">新對話</button>
    </div>
  </header>
</template>

<script setup lang="ts">
const s = useSessionStore()
async function onNew() {
  try { await s.newSession() } catch { s.notice = '開新對話失敗：連不到後端。' }
}
</script>
