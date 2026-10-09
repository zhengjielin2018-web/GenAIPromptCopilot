<template>
  <section v-if="button.visible || slot" class="mt-4" data-section="render">
    <h4 class="text-xs font-bold">預覽</h4>
    <div v-if="button.visible" class="mt-1.5 flex flex-wrap items-center gap-3">
      <button type="button" :disabled="button.disabled"
              class="rounded-md border-2 border-ink px-3 py-1 text-sm font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
              @click="s.requestRender(turnIndex)">
        生成預覽
      </button>
      <span v-if="button.note" class="text-xs text-muted">{{ button.note }}</span>
      <span v-if="realistic" class="text-xs text-muted">預覽會是動漫風</span>
    </div>
    <p v-if="slot?.error" class="mt-1.5 text-xs text-magenta">{{ slot.error }}</p>
    <p v-else-if="slot?.expired" class="mt-1.5 text-xs text-muted">預覽已過期</p>
    <template v-else-if="view">
      <p v-if="line" class="mt-1.5 text-xs" :class="view.status === 'failed' || view.status === 'blocked' ? 'text-magenta' : 'text-muted'">{{ line }}</p>
      <div v-if="showsImage(view.status)" class="mt-2">
        <p v-if="view.safety === 'off'" class="mb-1 text-[11px] font-medium text-magenta">審查已關閉（測試用）</p>
        <img :src="api.renderImageUrl(s.state.sessionId!, view.renderId)" alt="這份定稿的預覽圖" class="max-h-[32rem] rounded-md border border-rule">
        <p v-if="view.status === 'self_checking'" class="mt-2 text-xs text-muted">自評中…</p>
        <p v-else-if="view.selfCheck.status === 'unavailable'" class="mt-2 text-xs text-muted">這張的自評無法進行</p>
        <p v-else-if="view.selfCheck.items.length === 0" class="mt-2 text-xs text-muted">沒有可檢查的項目</p>
        <ul v-else class="mt-2 flex flex-col gap-1 text-xs">
          <li v-for="i in view.selfCheck.items" :key="i.facetId" class="flex items-baseline gap-2">
            <span class="w-3 shrink-0 text-center font-bold" :class="i.verdict === 'present' ? 'text-cyan' : i.verdict === 'absent' ? 'text-magenta' : 'text-muted'">{{ verdictMark(i.verdict) }}</span>
            <span class="shrink-0 font-medium">{{ i.label }}</span>
            <span class="shrink-0 font-mono text-[11px] text-muted">{{ i.tag }}</span>
            <span class="text-ink/80">{{ i.reason }}</span>
          </li>
        </ul>
      </div>
    </template>
    <p v-else-if="slot?.requesting || slot?.renderId" class="mt-1.5 text-xs text-muted">排隊中</p>
  </section>
</template>

<script setup lang="ts">
import type { FinalizedData } from '../types/api'
import { isRealistic, renderButton, showsImage, statusText, verdictMark } from '../lib/render'
/** 定稿卡的預覽區（預覽設計 §8）：按鈕只在最新的卡；舊卡只顯示它自己生過的那張。 */
const props = defineProps<{ data: FinalizedData; turnIndex: number }>()
const s = useSessionStore()
const api = useApi()
const slot = computed(() => s.renders[props.turnIndex] ?? null)
const view = computed(() => slot.value?.view ?? null)
const line = computed(() => (view.value ? statusText(view.value) : ''))
const button = computed(() => renderButton({ enabled: s.renderEnabled, isLatest: s.latestFinalizedTurn === props.turnIndex, busy: s.busy, inFlight: s.renderInFlight }))
const realistic = computed(() => isRealistic(s.state.facetTags, props.data.positive))
</script>
