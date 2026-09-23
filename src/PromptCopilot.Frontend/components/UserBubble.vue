<template>
  <div class="flex justify-end">
    <div data-role="user" class="max-w-[80%] whitespace-pre-wrap rounded-[14px] rounded-br-[4px] bg-ink px-4 py-2 text-sm leading-6 text-paper">{{ text }}</div>
  </div>
</template>

<script setup lang="ts">
defineProps<{ text: string }>()
</script>
