<template>
  <section ref="root" data-section="recommendations" class="mt-4 border-t border-rule pt-3">
    <h4 class="text-xs font-bold">
      <button type="button" data-action="toggle-recommendations" class="group flex w-full items-baseline gap-2 text-left"
              :aria-expanded="open" @click="open = !open">
        <span>參考組合</span>
        <span class="text-[11px] font-normal text-muted">{{ recs.dimensions.length }} 個維度</span>
        <span class="ml-auto shrink-0 text-[11px] font-normal text-muted group-hover:text-ink">{{ open ? '收起' : '展開' }}</span>
      </button>
    </h4>
    <div v-if="open">
      <p class="mt-0.5 text-[11px] text-muted">知識庫裡真實存在、有圖的整套設定。圖片來自來源網站，著作權屬原作者，點圖看出處。</p>
      <div v-for="d in recs.dimensions" :key="d.dimension" class="mt-2.5" :data-dimension="d.dimension">
        <p class="flex flex-wrap items-baseline gap-x-2 text-xs">
          <span class="font-medium">{{ d.label }}</span>
          <!-- 定稿卡（有 batch）理由看每一套；追問卡照舊顯示列層級文案 -->
          <span v-if="d.batch == null" class="text-[11px] text-muted">{{ recommendationLead(d) }}</span>
        </p>
        <ul class="mt-1.5 flex gap-2 overflow-x-auto pb-1">
          <template v-for="(set, i) in d.sets" :key="`${set.batch ?? 1}-${set.presetId}`">
            <li v-if="i > 0 && set.batch != null && set.batch !== d.sets[i - 1].batch" class="flex shrink-0 flex-col items-center justify-start gap-1 pt-10" data-batch-divider>
              <span class="h-16 w-px bg-rule" />
              <span class="text-[10px] text-muted">第 {{ set.batch }} 批</span>
            </li>
            <li class="w-28 shrink-0" :data-reason="set.reason ?? undefined">
              <button type="button" class="block w-full text-left" :title="set.title" @click="s.openDrawer(set.presetId)">
                <span v-if="set.imageUrl && !broken.has(set.presetId)" class="relative block h-28 w-28 rounded-[3px]"
                      :class="set.reason === 'explore' ? 'outline-dashed outline-1 outline-offset-2 outline-ink/60' : ''">
                  <img :src="set.imageUrl" :alt="set.title" class="h-28 w-28 rounded-[3px] object-cover" loading="lazy"
                       referrerpolicy="no-referrer" @error="broken.add(set.presetId)">
                  <span v-if="sourceName(set.sourceRef)"
                        class="absolute bottom-0.5 right-0.5 rounded-[2px] bg-ink/70 px-1 text-[9px] leading-4 text-paper">{{ sourceName(set.sourceRef) }}</span>
                </span>
                <div v-else class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule text-xs text-muted">無圖</div>
                <span class="mt-1 block truncate text-[11px] text-ink">{{ set.title }}</span>
                <span v-if="setReasonLabel(set)" class="block truncate text-[10px] text-muted" :title="setReasonLabel(set)!">{{ setReasonLabel(set) }}</span>
              </button>
              <button type="button" :disabled="!adoptable" :title="adoptable ? '逐項選擇要照它的' : '這一輪還在跑，跑完再採用'"
                      class="mt-1 w-full rounded-md border border-ink/80 px-2 py-1 text-[11px] font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                      @click="s.openAdopt(set, d.dimension, turnIndex)">採用</button>
            </li>
          </template>
          <li v-if="d.batch != null && adoptable" class="w-28 shrink-0">
            <p v-if="batchOf(d.dimension) === 'exhausted'" class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule p-2 text-center text-[11px] text-muted">這個維度沒有更多了</p>
            <p v-else-if="batchOf(d.dimension) === 'stale'" class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule p-2 text-center text-[11px] text-muted">這張卡已不能換一批</p>
            <button v-else type="button" data-action="next-batch" :disabled="batchOf(d.dimension) === 'loading'"
                    class="flex h-28 w-28 flex-col items-center justify-center gap-1 rounded-[3px] border border-ink/60 text-xs hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                    @click="nextBatch(d.dimension)">
              <span>{{ batchOf(d.dimension) === 'loading' ? '換一批中…' : '換一批' }}</span>
              <span v-if="batchOf(d.dimension) === 'error'" class="text-[10px] text-magenta">換一批失敗，再按一次</span>
            </button>
          </li>
        </ul>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import type { Recommendations } from '../types/api'
import type { FinalEntry } from '../lib/reducer'
import { recommendationLead, setReasonLabel, sourceName } from '../lib/copy'
/** recs：該輪的 recommendations 事件；turnIndex：卡片的輪次。只畫在最新一張定稿卡（FinalCard 擋），回合在跑時不能採用與換一批。 */
const props = defineProps<{ recs: Recommendations; turnIndex: number }>()
const s = useSessionStore()
/** 預設收合；沒展開就不載入第三方的圖。只存在元件裡，新的定稿卡與重新整理後都是收合的。 */
const open = ref(false)
const broken = reactive(new Set<number>())
const adoptable = computed(() => s.latestFinalizedTurn === props.turnIndex && !s.busy)
function batchOf(dimension: string) { return s.batchState[`${props.turnIndex}:${dimension}`] }

const root = ref<HTMLElement | null>(null)
/** 換一批接上新的一批後，只把那一排橫向捲到新批的分隔線；block: 'nearest' 不動整頁的垂直位置。 */
async function nextBatch(dimension: string) {
  // 讀 store 而不是 props：props 要等父層下一次 render 才更新
  const batchNow = () => s.state.transcript.findLast((e): e is FinalEntry => e.kind === 'final' && e.turnIndex === props.turnIndex)
    ?.recommendations?.dimensions.find(d => d.dimension === dimension)?.batch
  const before = batchNow()
  await s.nextBatch(props.turnIndex, dimension)
  if (batchNow() === before) return
  await nextTick()
  const dividers = root.value?.querySelectorAll(`[data-dimension="${dimension}"] [data-batch-divider]`)
  dividers?.[dividers.length - 1]?.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'start' })
}
</script>
