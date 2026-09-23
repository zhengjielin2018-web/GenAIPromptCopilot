<template>
  <div class="flex flex-wrap gap-1.5">
    <span v-for="o in options" :key="o.label" class="inline-flex items-stretch overflow-hidden rounded-md border text-xs transition-colors"
          :class="selected(o) ? 'border-ink' : light ? 'border-rule/70' : 'border-rule'">
      <button type="button" class="px-2.5 py-1.5"
              :class="selected(o) ? 'bg-ink text-paper' : light ? 'text-muted hover:text-ink' : 'bg-surface hover:bg-paper'"
              :title="o.tags" :aria-pressed="selected(o)" @click="s.toggleChip({ dimension, label: o.label })">
        {{ o.label }}
      </button>
      <button v-if="o.presetId !== null" type="button" title="看這個方向的範例" aria-label="看這個方向的範例"
              class="flex items-center border-l px-1.5 text-muted hover:bg-paper hover:text-ink"
              :class="selected(o) ? 'border-ink bg-surface' : 'border-rule'"
              @click="s.openDrawer(o.presetId!)">
        <svg viewBox="0 0 16 16" class="h-3 w-3" aria-hidden="true"><rect x="2" y="3" width="12" height="10" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.4" /><path d="M2.5 11l3.5-3.5 2.5 2.5 2-2 3 3" fill="none" stroke="currentColor" stroke-width="1.4" /></svg>
      </button>
    </span>
  </div>
</template>

<script setup lang="ts">
import type { OptionItem } from '../types/api'
const props = defineProps<{ options: OptionItem[]; dimension: string | null; light?: boolean }>()
const s = useSessionStore()
const selected = (o: OptionItem) => s.isChipSelected({ dimension: props.dimension, label: o.label })
</script>
