<template>
  <div class="max-w-[85%]">
    <div class="whitespace-pre-wrap rounded-2xl rounded-bl-sm border border-neutral-200 bg-white px-4 py-2 text-sm dark:border-neutral-800 dark:bg-neutral-900">{{ data.message }}</div>
    <div v-if="data.options?.length" class="mt-1.5 pl-1">
      <p class="mb-1 text-[11px] text-neutral-500">參考方向</p>
      <OptionChips :options="data.options" :dimension="null" light />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
defineProps<{ data: Extract<FinalData, { kind: 'message' }> }>()
</script>
