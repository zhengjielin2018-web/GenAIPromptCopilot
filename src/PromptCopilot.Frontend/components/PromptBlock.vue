<template>
  <section class="mt-4">
    <div class="flex items-baseline justify-between">
      <h4 class="text-xs font-bold">{{ label }}</h4>
      <button type="button" class="rounded px-1.5 text-xs text-muted hover:text-ink" @click="copy">{{ copied ? '已複製' : '複製' }}</button>
    </div>
    <pre class="mt-1.5 whitespace-pre-wrap break-words rounded-[4px] bg-paper px-3 py-2.5 font-mono text-[12.5px] leading-[1.7]">{{ text }}</pre>
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
