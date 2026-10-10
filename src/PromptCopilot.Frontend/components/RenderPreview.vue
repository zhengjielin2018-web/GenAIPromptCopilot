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
    <p v-if="slot?.requestError" class="mt-1.5 text-xs text-magenta">{{ slot.requestError }}</p>
    <p v-if="slot?.expired" class="mt-1.5 text-xs text-muted">預覽已過期</p>
    <template v-else-if="view">
      <p v-if="line" class="mt-1.5 text-xs" :class="view.status === 'failed' || view.status === 'blocked' ? 'text-magenta' : 'text-muted'">{{ line }}</p>
      <div v-if="showsImage(view.status)" class="mt-2">
        <p v-if="view.safety === 'off'" class="mb-1 text-[11px] font-medium text-magenta">審查已關閉（測試用）</p>
        <img :src="api.renderImageUrl(s.state.sessionId!, view.renderId)" alt="這份定稿的預覽圖" class="max-h-[32rem] rounded-md border border-rule">
        <p class="mt-1 text-[11px] text-muted">seed {{ view.seed }}</p>
        <p v-if="view.status === 'self_checking'" class="mt-2 text-xs text-muted">評分中…</p>
        <p v-else-if="view.selfCheck.status === 'unavailable'" class="mt-2 text-xs text-muted">這張的評分無法進行</p>
        <div v-else class="mt-2" data-section="self-check">
          <p v-if="view.selfCheck.score !== null" class="text-base font-bold">符合度 {{ view.selfCheck.score }}</p>
          <p class="text-xs text-ink/80">{{ view.selfCheck.summary }}</p>
          <div v-if="sg && (sg.text || sg.notes.length)" class="mt-2" data-section="suggestion">
            <div v-if="sg.text" class="flex flex-wrap items-center gap-2 text-xs">
              <span class="font-medium">{{ sg.text }}</span>
              <button v-if="fix.visible" type="button" :disabled="fix.disabled"
                      class="rounded-md border border-ink px-2 py-0.5 font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
                      @click="s.followSuggestion(turnIndex)">
                {{ fix.label }}
              </button>
              <span v-if="fix.visible && fix.note" class="text-muted">{{ fix.note }}</span>
            </div>
            <p v-for="n in sg.notes" :key="n" class="text-xs text-muted">{{ n }}</p>
          </div>
          <template v-if="groups.user.length">
            <h5 class="mt-2 text-xs font-bold">你的要求</h5>
            <SelfCheckList :items="groups.user" class="mt-1" />
          </template>
          <details v-if="groups.delegated.length" class="mt-2">
            <summary class="cursor-pointer text-xs text-muted">模型幫你挑的（不計分，{{ groups.delegated.length }} 條）</summary>
            <SelfCheckList :items="groups.delegated" class="mt-1" />
          </details>
        </div>
      </div>
      <p v-if="slot?.error" class="mt-1.5 text-xs text-magenta">{{ slot.error }}</p>
    </template>
    <p v-else-if="slot?.error" class="mt-1.5 text-xs text-magenta">{{ slot.error }}</p>
    <p v-else-if="slot?.requesting || slot?.renderId" class="mt-1.5 text-xs text-muted">排隊中</p>
  </section>
</template>

<script setup lang="ts">
import type { FinalizedData } from '../types/api'
import { isRealistic, renderButton, showsImage, splitItems, statusText, suggestionButton } from '../lib/render'
/** 定稿卡的預覽區（預覽設計 §8）：按鈕只在最新的卡；舊卡只顯示它自己生過的那張。 */
const props = defineProps<{ data: FinalizedData; turnIndex: number }>()
const s = useSessionStore()
const api = useApi()
const slot = computed(() => s.renders[props.turnIndex] ?? null)
const view = computed(() => slot.value?.view ?? null)
const line = computed(() => (view.value ? statusText(view.value) : ''))
const groups = computed(() => splitItems(view.value?.selfCheck.items ?? []))
const button = computed(() => renderButton({ enabled: s.renderEnabled, isLatest: s.latestFinalizedTurn === props.turnIndex, busy: s.busy, inFlight: s.renderInFlight }))
const realistic = computed(() => isRealistic(s.state.facetTags, props.data.positive))
const sg = computed(() => view.value?.selfCheck.suggestion ?? null)
const fix = computed(() => suggestionButton({
  kind: sg.value?.kind ?? 'none', isLatest: s.latestFinalizedTurn === props.turnIndex,
  busy: s.busy, inFlight: s.renderInFlight, sent: slot.value?.fixSent ?? false,
}))
</script>
