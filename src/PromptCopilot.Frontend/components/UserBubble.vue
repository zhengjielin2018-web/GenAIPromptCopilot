<template>
  <div class="flex justify-end">
    <div class="max-w-[80%] whitespace-pre-wrap rounded-2xl rounded-br-sm bg-neutral-900 px-4 py-2 text-sm text-white dark:bg-neutral-100 dark:text-neutral-900">{{ text }}</div>
  </div>
</template>

<script setup lang="ts">
defineProps<{ text: string }>()
</script>
