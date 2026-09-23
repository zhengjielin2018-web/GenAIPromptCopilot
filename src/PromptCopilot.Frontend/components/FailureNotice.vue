<template>
  <div data-card="failure" class="relative rounded-lg bg-magenta-wash py-3 pl-5 pr-4">
    <span class="absolute inset-y-3 left-0 w-[3px] rounded-full bg-magenta" aria-hidden="true" />
    <p class="text-xs font-bold text-magenta">{{ failureTitle(entry.source, entry.code) }}</p>
    <p class="mt-1 text-sm leading-6">{{ entry.message }}</p>
    <blockquote v-if="entry.originalText" class="mt-2 whitespace-pre-wrap text-xs text-muted">你送出的是：{{ entry.originalText }}</blockquote>
    <button v-if="entry.originalText" type="button" :disabled="s.busy"
            class="mt-2.5 rounded-md border border-magenta/60 px-3 py-1 text-xs font-medium text-magenta hover:bg-magenta hover:text-paper disabled:opacity-40"
            @click="s.retry(entry.originalText)">重試：把原文放回輸入框</button>
  </div>
</template>

<script setup lang="ts">
import type { FailureEntry } from '../lib/reducer'
import { failureTitle } from '../lib/copy'
defineProps<{ entry: FailureEntry }>()
const s = useSessionStore()
</script>
