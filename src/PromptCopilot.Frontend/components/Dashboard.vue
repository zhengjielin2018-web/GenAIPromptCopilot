<template>
  <aside class="flex h-full flex-col gap-3 overflow-y-auto p-4">
    <header class="flex items-baseline justify-between">
      <h2 class="text-sm font-semibold tracking-wide text-neutral-500">六維度儀表板</h2>
      <span class="text-xs text-neutral-500">追問 {{ s.state.askCount }}/{{ s.state.askLimit }}</span>
    </header>
    <p class="text-xs" :class="s.state.profile ? 'text-neutral-700 dark:text-neutral-300' : 'text-neutral-400'">
      題材：{{ profileLabel }}
    </p>
    <ul class="flex flex-col gap-2" :class="{ 'opacity-50': !s.state.profile }">
      <li v-for="row in rows" :key="row.key"
          class="rounded-lg border p-2 transition-colors"
          :class="[row.highlighted ? 'border-amber-400 bg-amber-50 dark:bg-amber-950/30' : 'border-neutral-200 dark:border-neutral-800',
                   row.applicable || !s.state.profile ? '' : 'opacity-30']">
        <div class="mb-1 flex items-center justify-between text-xs font-medium">
          <span>{{ row.label }}</span>
          <span v-if="s.state.profile && !row.applicable" class="text-neutral-400">不適用</span>
        </div>
        <div class="flex flex-wrap gap-1">
          <span v-for="c in row.chips" :key="c.id" :title="`${c.label}：${stateLabel(c.state)}`"
                class="rounded-full border px-2 py-0.5 text-[11px] leading-4" :class="chipClass(c.state)">
            {{ c.label }}<span v-if="c.state === 'waived'" aria-hidden="true"> ·略</span>
          </span>
        </div>
      </li>
    </ul>
  </aside>
</template>

<script setup lang="ts">
import { dashboardRows } from '../lib/dashboard'
import type { FacetState } from '../types/api'

const s = useSessionStore()
const rows = computed(() => s.catalog ? dashboardRows(s.catalog, s.state.profile, s.state.facetStates, s.state.highlighted) : [])
const PROFILE_LABELS: Record<string, string> = { portrait: '人像', landscape: '風景', object: '物件', vehicle: '載具' }
const profileLabel = computed(() => s.state.profile ? (PROFILE_LABELS[s.state.profile] ?? s.state.profile) : '尚未判定')

function stateLabel(st: FacetState) {
  return { covered: '已涵蓋', missing: '未提供', waived: '使用者略過', notApplicable: '不適用' }[st]
}
/** 四態不只靠顏色：實心／空心虛線／極淡／去飽和加刪除線與「略」。 */
function chipClass(st: FacetState) {
  switch (st) {
    case 'covered': return 'border-emerald-600 bg-emerald-600 text-white font-medium'
    case 'missing': return 'border-neutral-400 bg-transparent text-neutral-700 dark:text-neutral-300 border-dashed'
    case 'waived': return 'border-neutral-300 bg-neutral-300 text-neutral-600 line-through decoration-neutral-500 dark:bg-neutral-700 dark:border-neutral-700 dark:text-neutral-300'
    case 'notApplicable': return 'border-neutral-200 text-neutral-300 opacity-40 dark:border-neutral-800 dark:text-neutral-700'
  }
}
</script>
