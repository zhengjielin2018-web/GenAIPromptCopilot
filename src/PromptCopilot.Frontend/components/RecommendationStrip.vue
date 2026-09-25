<template>
  <section data-section="recommendations" class="mt-4 border-t border-rule pt-3">
    <h4 class="text-xs font-bold">參考組合</h4>
    <p class="mt-0.5 text-[11px] text-muted">知識庫裡真實存在、有圖的整套設定。圖片來自來源網站，著作權屬原作者，點圖看出處。</p>
    <div v-for="d in recs.dimensions" :key="d.dimension" class="mt-2.5">
      <p class="flex flex-wrap items-baseline gap-x-2 text-xs">
        <span class="font-medium">{{ d.label }}</span>
        <span class="text-[11px] text-muted">{{ d.anchored ? `含你講的 ${d.anchorTags.join(', ')}` : '最接近你描述的組合' }}</span>
      </p>
      <ul class="mt-1.5 flex gap-2 overflow-x-auto pb-1">
        <li v-for="set in d.sets" :key="set.presetId" class="w-28 shrink-0">
          <button type="button" class="block w-full text-left" :title="set.title" @click="s.openDrawer(set.presetId)">
            <span v-if="set.imageUrl && !broken.has(set.presetId)" class="relative block h-28 w-28">
              <img :src="set.imageUrl" :alt="set.title" class="h-28 w-28 rounded-[3px] object-cover" loading="lazy"
                   referrerpolicy="no-referrer" @error="broken.add(set.presetId)">
              <span v-if="sourceName(set.sourceRef)"
                    class="absolute bottom-0.5 right-0.5 rounded-[2px] bg-ink/70 px-1 text-[9px] leading-4 text-paper">{{ sourceName(set.sourceRef) }}</span>
            </span>
            <div v-else class="flex h-28 w-28 items-center justify-center rounded-[3px] border border-dashed border-rule text-xs text-muted">無圖</div>
            <span class="mt-1 block truncate text-[11px] text-ink">{{ set.title }}</span>
          </button>
          <button type="button" :disabled="!adoptable" :title="adoptable ? '逐項選擇要照它的' : '已有新的結果，這張卡的推薦不能再採用'"
                  class="mt-1 w-full rounded-md border border-ink/80 px-2 py-1 text-[11px] font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                  @click="s.openAdopt(set, d.dimension, turnIndex)">採用</button>
        </li>
      </ul>
    </div>
  </section>
</template>

<script setup lang="ts">
import type { Recommendations } from '../types/api'
import { sourceName } from '../lib/copy'
/** recs：該輪的 recommendations 事件；turnIndex：卡片的輪次。只有最新一張追問卡／定稿卡可以採用（舊卡的狀態已失效）。 */
const props = defineProps<{ recs: Recommendations; turnIndex: number }>()
const s = useSessionStore()
const broken = reactive(new Set<number>())
const adoptable = computed(() => s.latestRecommendableTurn === props.turnIndex && !s.busy)
</script>
