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
      <template v-if="presetsDetail">
        <ul class="mt-1 flex flex-col gap-1">
          <li v-for="(it, i) in presetsDetail.items" :key="i">
            <p v-if="it.error" class="text-magenta">{{ it.label }}｜{{ it.error }}</p>
            <template v-else>
              <button type="button" class="flex w-full items-baseline gap-2 text-left hover:bg-surface" :class="it.grounded ? 'text-ink' : 'text-muted'"
                      :aria-expanded="openItems.has(i)" @click="toggleItem(i)">
                <span class="shrink-0 font-medium">{{ it.label }}</span>
                <span class="truncate">{{ it.query }}</span>
                <span class="ml-auto shrink-0 tabular-nums">池 {{ it.poolSize }} → {{ it.hits.length }}</span>
                <span v-if="!it.grounded" class="shrink-0 text-[10px]">僅供建議</span>
              </button>
              <ul v-if="openItems.has(i)" class="ml-3 mt-0.5 flex flex-col gap-0.5 border-l border-rule pl-2 text-[11px]">
                <li v-for="h in it.hits" :key="h.id" class="flex items-baseline gap-2">
                  <button type="button" class="truncate text-left hover:text-cyan" @click="s.openDrawer(h.id)">{{ h.title }}</button>
                  <span class="ml-auto shrink-0 tabular-nums text-muted">{{ h.band }}・{{ h.dist.toFixed(3) }}・{{ h.usable ? '可借入' : '僅供建議' }}</span>
                </li>
                <li v-if="it.hits.length === 0" class="text-muted">沒有命中</li>
              </ul>
            </template>
          </li>
        </ul>
      </template>
      <ul v-else-if="similarDetail" class="mt-1 flex flex-col gap-0.5 text-[11px]">
        <li v-for="(h, i) in similarDetail.hits" :key="i" class="flex items-baseline gap-2">
          <span class="truncate">{{ h.intent }}</span>
          <span class="ml-auto shrink-0 tabular-nums text-muted">{{ h.profile }}・{{ h.dist.toFixed(3) }}</span>
        </li>
        <li v-if="similarDetail.hits.length === 0" class="text-muted">沒有相似作品</li>
      </ul>
      <p v-else-if="entry.summary" class="mt-1 text-ink">{{ entry.summary }}</p>
      <template v-if="entry.presets.length">
        <p class="mt-2 text-[11px] text-muted">圖片來自來源網站，著作權屬原作者，點圖看出處</p>
        <ul class="mt-1 flex gap-2 overflow-x-auto pb-1">
          <li v-for="p in entry.presets" :key="p.id">
            <button type="button" class="block w-24 text-left" @click="s.openDrawer(p.id)">
              <span v-if="p.imageUrl && !broken.has(p.id)" class="relative block h-24 w-24">
                <img :src="p.imageUrl" :alt="p.title" class="h-24 w-24 rounded-[3px] object-cover" loading="lazy"
                     referrerpolicy="no-referrer" @error="broken.add(p.id)">
                <span v-if="sourceName(p.sourceRef)"
                      class="absolute bottom-0.5 right-0.5 rounded-[2px] bg-ink/70 px-1 text-[9px] leading-4 text-paper">{{ sourceName(p.sourceRef) }}</span>
              </span>
              <div v-else class="flex h-24 w-24 items-center justify-center rounded-[3px] border border-dashed border-rule text-muted">無圖</div>
              <span class="mt-1 block truncate text-[11px] text-ink">{{ p.title }}</span>
            </button>
          </li>
        </ul>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import type { ToolEntry } from '../lib/reducer'
import { sourceName } from '../lib/copy'
import { isPresetsDetail, isSimilarDetail } from '../types/api'
const props = defineProps<{ entry: ToolEntry }>()
const s = useSessionStore()
const open = ref(false)
const broken = reactive(new Set<number>())
const TITLES: Record<string, string> = {
  SearchPresets: '查知識庫', SearchSimilarPrompts: '找相似作品', SetProfile: '判定題材', SetFacetStates: '更新維度狀態',
}
const title = computed(() => TITLES[props.entry.name] ?? props.entry.name)
const openItems = reactive(new Set<number>())
function toggleItem(i: number) { if (openItems.has(i)) openItems.delete(i); else openItems.add(i) }
/** 只有「顯示檢索細節」開啟且事件帶 detail 才展開逐項；否則退回一行摘要（舊的 sessionStorage 資料沒有 detail）。 */
const presetsDetail = computed(() => s.prefs.showTrace && isPresetsDetail(props.entry.name, props.entry.detail) ? props.entry.detail : null)
const similarDetail = computed(() => s.prefs.showTrace && isSimilarDetail(props.entry.name, props.entry.detail) ? props.entry.detail : null)
</script>
