<template>
  <div class="rounded-2xl border border-red-300 bg-red-50 p-3 dark:border-red-900 dark:bg-red-950/20">
    <p class="text-xs font-semibold text-red-700 dark:text-red-300">{{ failureTitle(entry.source, entry.code) }}</p>
    <p class="mt-1 text-sm">{{ entry.message }}</p>
    <blockquote v-if="entry.originalText" class="mt-2 whitespace-pre-wrap border-l-2 border-red-300 pl-2 text-xs text-neutral-600 dark:text-neutral-400">{{ entry.originalText }}</blockquote>
    <button v-if="entry.originalText" type="button" :disabled="s.busy"
            class="mt-2 rounded-md border border-red-400 px-3 py-1 text-xs text-red-700 hover:bg-red-100 disabled:opacity-40 dark:text-red-300 dark:hover:bg-red-950/40"
            @click="s.retry(entry.originalText)">重試（把原文填回輸入框）</button>
  </div>
</template>

<script setup lang="ts">
import type { FailureEntry } from '../lib/reducer'
import { failureTitle } from '../lib/copy'
defineProps<{ entry: FailureEntry }>()
const s = useSessionStore()
</script>
