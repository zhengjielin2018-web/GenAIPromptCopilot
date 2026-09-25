<template>
  <aside data-panel="dashboard" class="flex h-full flex-col overflow-y-auto bg-paper px-5 py-5">
    <header>
      <div class="flex items-baseline justify-between gap-2">
        <h2 class="text-sm font-bold">畫面資訊</h2>
        <span class="text-xs tabular-nums text-muted">追問 {{ s.state.askCount }}/{{ s.state.askLimit }}</span>
      </div>
      <p class="mt-1 text-xs text-muted">
        題材 <span class="font-medium" :class="s.state.profile ? 'text-ink' : 'text-muted'">{{ profileLabel }}</span>
        <template v-if="s.state.profile">，已涵蓋 <span class="font-medium tabular-nums text-ink">{{ coverage.covered }}/{{ coverage.total }}</span></template>
      </p>
      <!-- 涵蓋比例：一條墨線，隨輪次變長 -->
      <div class="mt-2 h-1 overflow-hidden rounded-full bg-rule" aria-hidden="true">
        <div class="h-full bg-ink transition-[width] duration-500" :style="{ width: `${coverage.total ? (coverage.covered / coverage.total) * 100 : 0}%` }" />
      </div>
    </header>

    <ul class="mt-4 flex flex-col gap-3" :class="{ 'opacity-60': !s.state.profile }">
      <li v-for="row in rows" :key="row.key" :data-highlighted="row.highlighted"
          class="relative rounded-sm py-1.5 pl-3 pr-1 transition-colors"
          :class="[row.highlighted ? 'bg-yellow-wash' : '', row.applicable || !s.state.profile ? '' : 'opacity-40']">
        <span class="absolute inset-y-0 left-0 w-[3px] rounded-full" :class="row.highlighted ? 'bg-yellow' : 'bg-rule'" aria-hidden="true" />
        <div class="mb-1.5 flex items-baseline justify-between text-xs">
          <span class="font-medium">{{ row.label }}</span>
          <span v-if="row.highlighted" class="text-[11px] text-muted">正在問</span>
          <span v-else-if="s.state.profile && !row.applicable" class="text-[11px] text-muted">此題材不適用</span>
        </div>
        <div class="flex flex-wrap gap-1">
          <span v-for="c in row.chips" :key="c.id" :title="`${c.label}：${stateLabel(c.state)}`" :data-state="c.state"
                class="rounded-[3px] px-1.5 py-[3px] text-[11px] leading-4" :class="chipClass(c.state)">
            {{ c.label }}
          </span>
        </div>
      </li>
    </ul>

    <footer class="mt-auto pt-5">
      <section v-if="s.prefs.showTrace && trace.searches > 0" class="mb-4 border-t border-rule pt-4" data-section="trace">
        <h3 class="text-xs font-bold">本次對話檢索摘要</h3>
        <p class="mt-1.5 text-xs tabular-nums text-muted">
          查詢 {{ trace.searches }} 次・看過 {{ trace.seen }} 筆片段・借用 {{ trace.borrowed }} 筆
        </p>
        <div class="mt-1.5 flex flex-wrap gap-1">
          <span v-for="p in trace.pools" :key="p.dimension" class="rounded-[3px] border border-rule px-1.5 py-[3px] text-[11px] leading-4 tabular-nums">
            {{ p.label }} {{ p.poolSize }}
          </span>
        </div>
      </section>
      <ul class="grid grid-cols-2 gap-x-3 gap-y-1.5 text-[11px] text-muted" aria-label="圖例">
        <li v-for="st in legend" :key="st" class="flex items-center gap-1.5">
          <span class="inline-block h-3 w-5 rounded-[2px]" :class="chipClass(st)" aria-hidden="true" />{{ stateLabel(st) }}
        </li>
      </ul>
    </footer>
  </aside>
</template>

<script setup lang="ts">
import { dashboardRows } from '../lib/dashboard'
import { retrievalSummary } from '../lib/trace'
import type { FacetState } from '../types/api'

const s = useSessionStore()
const rows = computed(() => s.catalog ? dashboardRows(s.catalog, s.state.profile, s.state.facetStates, s.state.highlighted) : [])
const PROFILE_LABELS: Record<string, string> = { portrait: '人像', landscape: '風景', object: '物件', vehicle: '載具' }
const profileLabel = computed(() => s.state.profile ? (PROFILE_LABELS[s.state.profile] ?? s.state.profile) : '尚未判定')
const coverage = computed(() => {
  const chips = rows.value.filter(r => r.applicable).flatMap(r => r.chips).filter(c => c.state !== 'notApplicable')
  return { covered: chips.filter(c => c.state === 'covered').length, total: chips.length }
})
const legend: FacetState[] = ['covered', 'missing', 'waived', 'notApplicable']
const trace = computed(() => retrievalSummary(s.state.transcript))

function stateLabel(st: FacetState) {
  return { covered: '已涵蓋', missing: '還沒提到', waived: '你說不指定', notApplicable: '不適用' }[st]
}
/** 四態靠填法區分，不靠顏色：實心墨／虛線框／斜線網紋加刪除線／點線幾乎透明。 */
function chipClass(st: FacetState) {
  switch (st) {
    case 'covered': return 'bg-ink text-paper font-medium border border-ink'
    case 'missing': return 'border border-dashed border-muted/70 text-ink'
    case 'waived': return 'hatch border border-muted/50 text-muted line-through decoration-muted'
    case 'notApplicable': return 'border border-dotted border-rule text-muted/50'
  }
}
</script>
