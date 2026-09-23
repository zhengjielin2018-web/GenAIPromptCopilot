<template>
  <div class="flex h-full flex-col">
    <TopBar />
    <div v-if="s.bootError" class="m-auto max-w-sm text-center">
      <p class="text-sm leading-6">{{ s.bootError }}</p>
      <button type="button" class="mt-3 rounded-md bg-ink px-4 py-1.5 text-sm font-medium text-paper hover:bg-ink/85" @click="s.boot()">重試</button>
    </div>
    <div v-else class="relative grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[minmax(0,1fr)_19rem]">
      <section class="flex min-h-0 flex-col">
        <ChatStream class="min-h-0 flex-1" />
        <Composer />
      </section>
      <Dashboard class="hidden border-l border-rule md:flex" />
      <PresetDrawer />
    </div>
  </div>
</template>

<script setup lang="ts">
const s = useSessionStore()
onMounted(() => s.boot())
</script>
