<template>
  <div ref="el" class="flex flex-col gap-3 overflow-y-auto px-4 py-4">
    <p v-if="s.notice" class="rounded-md bg-neutral-100 px-3 py-2 text-xs text-neutral-600 dark:bg-neutral-900 dark:text-neutral-400">{{ s.notice }}</p>
    <p v-if="s.state.transcript.length === 0" class="m-auto max-w-sm text-center text-sm text-neutral-500">
      用繁體中文描述你想生成的畫面，例如「一個銀髮少女站在雨夜的霓虹街頭」。我會分析六個維度、追問缺的細節，最後給你英文 prompt。
    </p>
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
    <p v-if="s.busy" class="animate-pulse text-xs text-neutral-400">思考中…</p>
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
