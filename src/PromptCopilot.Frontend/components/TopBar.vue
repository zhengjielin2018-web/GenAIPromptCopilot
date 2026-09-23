<template>
  <header class="flex items-center justify-between border-b border-neutral-200 bg-white px-4 py-2 dark:border-neutral-800 dark:bg-neutral-900">
    <div>
      <h1 class="text-base font-semibold">Prompt Copilot</h1>
      <p class="text-[11px] text-neutral-500">多輪追問 → SD/SDXL 英文提示詞</p>
    </div>
    <button type="button" :disabled="s.busy || !!s.bootError"
            class="rounded-md border border-neutral-300 px-3 py-1 text-sm hover:bg-neutral-50 disabled:opacity-40 dark:border-neutral-700 dark:hover:bg-neutral-800"
            @click="onNew">新對話</button>
  </header>
</template>

<script setup lang="ts">
const s = useSessionStore()
async function onNew() {
  try { await s.newSession() } catch { s.notice = '開新對話失敗：連不到後端。' }
}
</script>
