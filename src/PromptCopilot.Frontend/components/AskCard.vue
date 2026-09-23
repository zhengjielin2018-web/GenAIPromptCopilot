<template>
  <div class="rounded-2xl border border-amber-300 bg-amber-50/60 p-4 dark:border-amber-700 dark:bg-amber-950/20">
    <p class="text-sm">{{ data.preamble }}</p>
    <section v-for="a in data.asks" :key="a.dimension" class="mt-3">
      <h3 class="text-xs font-semibold text-amber-800 dark:text-amber-300">{{ s.dimensionLabels[a.dimension] ?? a.dimension }}</h3>
      <p class="mt-0.5 text-sm">{{ a.question }}</p>
      <OptionChips class="mt-2" :options="a.options" :dimension="a.dimension" />
    </section>
    <p class="mt-3 text-[11px] text-neutral-500">點選項會填進輸入框，可以多選、可以再改，送出時才會送。</p>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
defineProps<{ data: Extract<FinalData, { kind: 'ask' }> }>()
const s = useSessionStore()
</script>
