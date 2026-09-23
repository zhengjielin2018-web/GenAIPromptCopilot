<template>
  <div data-card="ask" class="relative rounded-lg border border-rule bg-surface py-4 pl-5 pr-4">
    <span class="absolute inset-y-3 left-0 w-[3px] rounded-full bg-yellow" aria-hidden="true" />
    <p class="text-sm leading-6">{{ data.preamble }}</p>
    <section v-for="a in data.asks" :key="a.dimension" class="mt-4">
      <h3 class="flex items-center gap-1.5 text-xs font-bold">
        <span class="inline-block h-2 w-2 rounded-[1px] bg-yellow" aria-hidden="true" />{{ s.dimensionLabels[a.dimension] ?? a.dimension }}
      </h3>
      <p class="mt-1 text-sm leading-6">{{ a.question }}</p>
      <OptionChips class="mt-2" :options="a.options" :dimension="a.dimension" />
    </section>
    <p class="mt-4 text-[11px] text-muted">選項會填進下面的輸入框，可以多選、可以再改，按送出才會送。</p>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
defineProps<{ data: Extract<FinalData, { kind: 'ask' }> }>()
const s = useSessionStore()
</script>
