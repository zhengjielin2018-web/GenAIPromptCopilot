<template>
  <div class="text-xs">
    <button type="button" class="flex w-full items-center gap-2 rounded-md border border-neutral-200 bg-white px-2 py-1 text-left text-neutral-600 hover:bg-neutral-50 dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-400"
            :aria-expanded="open" @click="open = !open">
      <span aria-hidden="true">{{ icon }}</span>
      <span class="shrink-0 font-medium">{{ title }}</span>
      <span class="truncate text-neutral-400">{{ entry.done ? (entry.summary ?? '') : entry.argsSummary }}</span>
      <span v-if="!entry.done" class="ml-auto shrink-0 animate-pulse text-neutral-400">進行中…</span>
      <span v-else class="ml-auto shrink-0 text-neutral-400">{{ open ? '收起' : '展開' }}</span>
    </button>
    <div v-if="open" class="mt-1 rounded-md border border-neutral-100 bg-neutral-50 p-2 dark:border-neutral-800 dark:bg-neutral-950">
      <p v-if="entry.argsSummary" class="break-all font-mono text-[11px] text-neutral-500">{{ entry.argsSummary }}</p>
      <p v-if="entry.summary" class="mt-1">{{ entry.summary }}</p>
      <ul v-if="entry.presets.length" class="mt-2 flex gap-2 overflow-x-auto">
        <li v-for="p in entry.presets" :key="p.id">
          <button type="button" class="block w-24 text-left" @click="s.openDrawer(p.id)">
            <img v-if="p.imageUrl && !broken.has(p.id)" :src="p.imageUrl" :alt="p.title" class="h-24 w-24 rounded object-cover" loading="lazy"
                 referrerpolicy="no-referrer" @error="broken.add(p.id)">
            <div v-else class="flex h-24 w-24 items-center justify-center rounded bg-neutral-200 text-neutral-400 dark:bg-neutral-800">無圖</div>
            <span class="mt-1 block truncate text-[11px]">{{ p.title }}</span>
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ToolEntry } from '../lib/reducer'
const props = defineProps<{ entry: ToolEntry }>()
const s = useSessionStore()
const open = ref(false)
const broken = reactive(new Set<number>())
const TITLES: Record<string, [string, string]> = {
  SearchPresets: ['🔍', '查詢知識庫'], SearchSimilarPrompts: ['📚', '找相似作品'],
  SetProfile: ['🎯', '判定題材'], SetFacetStates: ['🧭', '更新維度狀態'],
}
const icon = computed(() => TITLES[props.entry.name]?.[0] ?? '⚙️')
const title = computed(() => TITLES[props.entry.name]?.[1] ?? props.entry.name)
</script>
