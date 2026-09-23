<template>
  <form class="border-t border-rule bg-surface px-5 py-3" @submit.prevent="s.send()">
    <div class="mx-auto flex w-full max-w-[46rem] items-end gap-2">
      <textarea ref="ta" :value="s.draft" rows="2" :disabled="s.busy || !!s.bootError" aria-label="描述你想要的畫面"
                placeholder="描述你想要的畫面（Enter 送出，Shift+Enter 換行）"
                class="min-h-[2.75rem] flex-1 resize-y rounded-md border border-rule bg-paper px-3 py-2 text-sm leading-6 placeholder:text-muted/70 focus:border-cyan focus:outline-none disabled:opacity-60"
                @input="s.setDraft(($event.target as HTMLTextAreaElement).value)"
                @keydown.enter.exact="onEnter" />
      <button type="submit" :disabled="s.busy || !s.draft.trim() || !!s.bootError"
              class="h-[2.75rem] rounded-md bg-ink px-5 text-sm font-medium text-paper hover:bg-ink/85 disabled:bg-rule disabled:text-muted">
        {{ s.busy ? '整理中' : '送出' }}
      </button>
    </div>
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
