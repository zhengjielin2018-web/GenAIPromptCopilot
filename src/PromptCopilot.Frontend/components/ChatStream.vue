<template>
  <div ref="el" class="overflow-y-auto px-5 py-6">
    <div class="mx-auto flex w-full max-w-[46rem] flex-col gap-3">
      <p v-if="s.notice" class="rounded-md border border-rule bg-surface px-3 py-2 text-xs text-muted">{{ s.notice }}</p>
      <div v-if="s.state.transcript.length === 0" class="mt-[18vh] max-w-md">
        <h2 class="text-2xl font-bold leading-snug">想生成什麼畫面？</h2>
        <p class="mt-3 text-sm leading-7 text-muted">
          用中文描述就好，例如「一個銀髮少女站在雨夜的霓虹街頭」。我會從風格、場景、鏡頭、人物樣貌、動作、穿著六個面向檢查，缺的細節會問你，最後給你可以直接貼進 Stable Diffusion 的英文提示詞。
        </p>
      </div>
      <template v-for="(e, i) in s.state.transcript" :key="i">
        <UserBubble v-if="e.kind === 'user'" :text="e.text" />
        <ToolCallCard v-else-if="e.kind === 'tool'" :entry="e" />
        <FailureNotice v-else-if="e.kind === 'failure'" :entry="e" />
        <template v-else-if="e.kind === 'final'">
          <AskCard v-if="e.data.kind === 'ask'" :data="e.data" />
          <MessageBubble v-else-if="e.data.kind === 'message'" :data="e.data" />
          <FinalCard v-else-if="e.data.kind === 'finalized'" :data="e.data" :turn-index="e.turnIndex" />
          <SaveConsentNotice v-else-if="e.data.kind === 'save_consent_requested'" />
        </template>
      </template>
      <p v-if="s.busy" class="flex items-center gap-2 text-xs text-muted">
        <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-cyan" aria-hidden="true" />整理中…
      </p>
    </div>
  </div>
</template>

<script setup lang="ts">
const s = useSessionStore()
const el = ref<HTMLElement | null>(null)

watch(() => [s.state.transcript.length, s.busy], async () => {
  await nextTick()
  el.value?.scrollTo({ top: el.value.scrollHeight, behavior: 'smooth' })
})

// save_consent_requested：展開最近一張定稿卡的確認區並捲過去
watch(() => s.state.transcript.at(-1), async (last) => {
  if (last?.kind !== 'final' || last.data.kind !== 'save_consent_requested' || s.latestFinalizedTurn === null) return
  s.expandSave(s.latestFinalizedTurn)
  await nextTick()
  document.getElementById(`save-${s.latestFinalizedTurn}`)?.scrollIntoView({ behavior: 'smooth', block: 'center' })
})
</script>
