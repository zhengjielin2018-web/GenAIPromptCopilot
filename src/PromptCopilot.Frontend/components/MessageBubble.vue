<template>
  <div class="max-w-[88%]">
    <div class="whitespace-pre-wrap rounded-[14px] rounded-bl-[4px] border border-rule bg-surface px-4 py-2.5 text-sm leading-6">{{ data.message }}</div>
    <div v-if="data.options?.length" class="mt-2 pl-1">
      <p class="mb-1.5 text-[11px] text-muted">可以參考的方向</p>
      <OptionChips :options="data.options" :dimension="null" light />
    </div>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
defineProps<{ data: Extract<FinalData, { kind: 'message' }> }>()
</script>
