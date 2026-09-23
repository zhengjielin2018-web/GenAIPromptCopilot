<template>
  <form class="flex items-end gap-2 border-t border-neutral-200 bg-white p-3 dark:border-neutral-800 dark:bg-neutral-900" @submit.prevent="s.send()">
    <textarea ref="ta" :value="s.draft" rows="2" :disabled="s.busy || !!s.bootError" aria-label="描述你想要的畫面"
              placeholder="描述你想要的畫面…（Enter 送出，Shift+Enter 換行）"
              class="min-h-[2.5rem] flex-1 resize-y rounded-md border border-neutral-300 bg-white px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-neutral-400 disabled:opacity-60 dark:border-neutral-700 dark:bg-neutral-950"
              @input="s.setDraft(($event.target as HTMLTextAreaElement).value)"
              @keydown.enter.exact="onEnter" />
    <button type="submit" :disabled="s.busy || !s.draft.trim() || !!s.bootError"
            class="rounded-md bg-neutral-900 px-4 py-2 text-sm text-white disabled:opacity-40 dark:bg-neutral-100 dark:text-neutral-900">
      {{ s.busy ? '進行中…' : '送出' }}
    </button>
  </form>
</template>

<script setup lang="ts">
// textarea 用 :value + @input 而不是 v-model：chip 組字由 store 改 draft；只有使用者手打才走 setDraft（把 draftDirty 設成 true）。
const s = useSessionStore()
const ta = ref<HTMLTextAreaElement | null>(null)

// 「重試」把原文填回來時聚焦並把游標放到最後
watch(() => s.draft, async (v, old) => {
  if (!v || old) return
  await nextTick()
  ta.value?.focus()
  ta.value?.setSelectionRange(v.length, v.length)
})

/** 中文輸入法選字時按 Enter 是「確定選字」不是送出：isComposing 為 true 就放過。 */
function onEnter(e: KeyboardEvent) {
  if (e.isComposing || e.keyCode === 229) return
  e.preventDefault()
  s.send()
}
</script>
