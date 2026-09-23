<template>
  <main class="p-6">
    <h1 class="text-xl font-semibold">Prompt Copilot</h1>
    <p class="mt-2 text-sm text-neutral-500">骨架已就緒；health = {{ health }}</p>
  </main>
</template>

<script setup lang="ts">
const health = ref('…')
onMounted(async () => {
  try {
    const r = await fetch(`${useRuntimeConfig().public.apiBase}/health`)
    health.value = r.ok ? 'ok' : `HTTP ${r.status}`
  } catch { health.value = '連不到後端' }
})
</script>
