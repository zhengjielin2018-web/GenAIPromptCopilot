<template>
  <ul class="flex flex-col gap-1 text-xs">
    <li v-for="i in items" :key="i.id" class="flex flex-wrap items-baseline gap-x-2">
      <span class="w-3 shrink-0 text-center font-bold" :class="i.verdict === 'met' ? 'text-cyan' : i.verdict === 'unmet' ? 'text-magenta' : 'text-muted'">{{ verdictMark(i.verdict) }}</span>
      <span class="shrink-0 font-medium">{{ i.text }}</span>
      <span class="shrink-0 font-mono text-[11px] text-muted">{{ tagText(i) }}</span>
      <span class="text-ink/80">{{ i.reason }}</span>
      <span v-if="issueLabel(i)" class="shrink-0 rounded border border-magenta px-1 text-[11px] text-magenta" :title="issueLabel(i)!.hint">{{ issueLabel(i)!.text }}</span>
    </li>
  </ul>
</template>

<script setup lang="ts">
import type { SelfCheckItemView } from '../types/api'
import { issueLabel, tagText, verdictMark } from '../lib/render'
/** 一份評分清單的列（符合度設計 §9）：「你的要求」與「模型幫你挑的」共用。 */
defineProps<{ items: SelfCheckItemView[] }>()
</script>
