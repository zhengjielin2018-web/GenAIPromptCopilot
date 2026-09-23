<template>
  <div class="flex flex-wrap gap-1.5">
    <span v-for="o in options" :key="o.label" class="inline-flex items-stretch overflow-hidden rounded-full border text-xs transition-colors"
          :class="selected(o) ? 'border-neutral-900 dark:border-neutral-100'
                              : light ? 'border-neutral-200 dark:border-neutral-800' : 'border-neutral-300 dark:border-neutral-700'">
      <button type="button" class="px-2.5 py-1"
              :class="selected(o) ? 'bg-neutral-900 text-white dark:bg-neutral-100 dark:text-neutral-900'
                                  : light ? 'text-neutral-600 hover:bg-neutral-100 dark:text-neutral-400 dark:hover:bg-neutral-800'
                                          : 'bg-white text-neutral-800 hover:bg-neutral-100 dark:bg-neutral-900 dark:text-neutral-200 dark:hover:bg-neutral-800'"
              :title="o.tags" :aria-pressed="selected(o)" @click="s.toggleChip({ dimension, label: o.label })">
        {{ o.label }}
      </button>
      <button v-if="o.presetId !== null" type="button" title="看這個方向的 preset"
              class="border-l border-inherit px-1.5 text-[10px] text-neutral-500 hover:bg-neutral-100 hover:text-neutral-900 dark:hover:bg-neutral-800 dark:hover:text-neutral-100"
              @click="s.openDrawer(o.presetId!)">↗</button>
    </span>
  </div>
</template>

<script setup lang="ts">
import type { OptionItem } from '../types/api'
const props = defineProps<{ options: OptionItem[]; dimension: string | null; light?: boolean }>()
const s = useSessionStore()
const selected = (o: OptionItem) => s.isChipSelected({ dimension: props.dimension, label: o.label })
</script>
