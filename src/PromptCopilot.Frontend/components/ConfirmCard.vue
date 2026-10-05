<template>
  <div data-card="confirm" class="relative rounded-lg border border-rule bg-surface py-4 pl-5 pr-4">
    <span class="absolute inset-y-3 left-0 w-[3px] rounded-full bg-cyan" aria-hidden="true" />
    <p class="whitespace-pre-wrap text-sm leading-6">{{ data.message }}</p>
    <div class="mt-3 flex flex-wrap gap-2">
      <button v-for="(label, i) in buttons" :key="i" type="button" :disabled="!active" data-action="confirm"
              :title="stale ? '已經有新的進展，這張卡不能再按' : undefined"
              class="rounded-md border border-ink/80 px-3 py-1.5 text-sm font-medium hover:bg-ink hover:text-paper disabled:border-rule disabled:text-muted disabled:hover:bg-transparent"
              @click="s.confirm(turnIndex, data.choices.length ? i : null)">{{ label }}</button>
    </div>
    <p class="mt-3 text-[11px] text-muted">不對的話，直接在下面打字修正。</p>
  </div>
</template>

<script setup lang="ts">
import type { FinalData } from '../types/api'
import { ACCEPT_TEXT } from '../lib/confirm'
/** 先確認再動手設計 §7：沒有選項時一顆「對，就這樣」，有選項時每個解讀一顆。只有最新一張、之後還沒動手的卡可以按。 */
const props = defineProps<{ data: Extract<FinalData, { kind: 'confirm' }>; turnIndex: number }>()
const s = useSessionStore()
const buttons = computed(() => (props.data.choices.length ? props.data.choices : [ACCEPT_TEXT]))
const stale = computed(() => s.pendingConfirm !== props.turnIndex)
const active = computed(() => !stale.value && !s.busy)
</script>
