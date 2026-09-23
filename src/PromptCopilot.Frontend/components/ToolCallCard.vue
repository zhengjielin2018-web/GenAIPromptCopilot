<template>
  <div class="text-xs">
    <button type="button" class="group flex w-full items-center gap-2 rounded px-1.5 py-1 text-left text-muted hover:bg-surface"
            :aria-expanded="open" @click="open = !open">
      <span class="h-3 w-[3px] shrink-0 rounded-full" :class="entry.done ? 'bg-rule group-hover:bg-cyan' : 'animate-pulse bg-cyan'" aria-hidden="true" />
      <span class="shrink-0 font-medium text-ink/80">{{ title }}</span>
      <span class="truncate">{{ entry.done ? (entry.summary ?? '') : entry.argsSummary }}</span>
      <span v-if="!entry.done" class="ml-auto shrink-0 text-cyan">進行中</span>
      <span v-else class="ml-auto shrink-0 opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100">{{ open ? '收起' : '細節' }}</span>
    </button>
    <div v-if="open" class="ml-[13px] mt-1 border-l border-rule pb-1 pl-3">
      <p v-if="entry.argsSummary" class="break-all font-mono text-[11px] text-muted">{{ entry.argsSummary }}</p>
      <p v-if="entry.summary" class="mt-1 text-ink">{{ entry.summary }}</p>
      <ul v-if="entry.presets.length" class="mt-2 flex gap-2 overflow-x-auto pb-1">
        <li v-for="p in entry.presets" :key="p.id">
          <button type="button" class="block w-24 text-left" @click="s.openDrawer(p.id)">
            <img v-if="p.imageUrl && !broken.has(p.id)" :src="p.imageUrl" :alt="p.title" class="h-24 w-24 rounded-[3px] object-cover" loading="lazy"
                 referrerpolicy="no-referrer" @error="broken.add(p.id)">
            <div v-else class="flex h-24 w-24 items-center justify-center rounded-[3px] border border-dashed border-rule text-muted">無圖</div>
            <span class="mt-1 block truncate text-[11px] text-ink">{{ p.title }}</span>
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
const TITLES: Record<string, string> = {
  SearchPresets: '查知識庫', SearchSimilarPrompts: '找相似作品', SetProfile: '判定題材', SetFacetStates: '更新維度狀態',
}
const title = computed(() => TITLES[props.entry.name] ?? props.entry.name)
</script>
