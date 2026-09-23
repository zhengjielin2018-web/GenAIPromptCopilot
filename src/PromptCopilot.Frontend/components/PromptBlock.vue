<template>
  <section class="mt-3">
    <div class="flex items-center justify-between">
      <h4 class="text-xs font-medium text-neutral-500">{{ label }}</h4>
      <button type="button" class="text-xs text-neutral-500 hover:text-neutral-900 dark:hover:text-neutral-100" @click="copy">{{ copied ? '已複製' : '複製' }}</button>
    </div>
    <pre class="mt-1 whitespace-pre-wrap break-words rounded bg-neutral-100 p-2 font-mono text-xs dark:bg-neutral-950">{{ text }}</pre>
  </section>
</template>

<script setup lang="ts">
const props = defineProps<{ label: string; text: string }>()
const copied = ref(false)
async function copy() {
  try { await navigator.clipboard.writeText(props.text); copied.value = true; setTimeout(() => (copied.value = false), 1500) }
  catch { /* 非 https 或權限被拒：使用者可以自己選取 */ }
}
</script>
