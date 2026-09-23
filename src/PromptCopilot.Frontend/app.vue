<template>
  <div class="flex h-full flex-col">
    <TopBar />
    <div v-if="s.bootError" class="m-auto max-w-sm text-center">
      <p class="text-sm">{{ s.bootError }}</p>
      <button type="button" class="mt-3 rounded-md border border-neutral-300 px-3 py-1 text-sm dark:border-neutral-700" @click="s.boot()">重試</button>
    </div>
    <div v-else class="relative grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[minmax(0,1fr)_20rem]">
      <section class="flex min-h-0 flex-col">
        <ChatStream class="min-h-0 flex-1" />
        <Composer />
      </section>
      <Dashboard class="hidden border-l border-neutral-200 md:block dark:border-neutral-800" />
      <PresetDrawer />
    </div>
  </div>
</template>

<script setup lang="ts">
const s = useSessionStore()
onMounted(() => s.boot())
</script>
