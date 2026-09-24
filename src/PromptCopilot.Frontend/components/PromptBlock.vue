<template>
  <section class="mt-4">
    <div class="flex items-baseline justify-between">
      <h4 class="text-xs font-bold">{{ label }}</h4>
      <button type="button" class="rounded px-1.5 text-xs text-muted hover:text-ink" @click="copy">{{ copied ? '已複製' : '複製' }}</button>
    </div>
    <template v-if="sources?.length">
      <ul class="mt-1.5 flex flex-wrap gap-1">
        <li v-for="(t, i) in sources" :key="i">
          <button v-if="t.origin === 'rag' && t.presetIds.length" type="button" :class="[CHIP, look(t), 'hover:bg-cyan hover:text-paper']"
                  :title="`來自〈${t.presetTitle ?? `片段 #${t.presetIds[0]}`}〉`" @click="s.openDrawer(t.presetIds[0])">
            {{ t.tag }}
          </button>
          <span v-else :class="[CHIP, look(t)]">{{ t.tag }}</span>
        </li>
      </ul>
      <p class="mt-1.5 flex flex-wrap gap-x-3 gap-y-1 text-[11px] text-muted">
        <span v-for="l in LEGEND" :key="l.origin" class="inline-flex items-center gap-1">
          <span class="inline-block h-2.5 w-2.5 rounded-[2px] border" :class="SWATCH[l.origin]" aria-hidden="true" />{{ l.label }}
        </span>
      </p>
    </template>
    <pre v-else class="mt-1.5 whitespace-pre-wrap break-words rounded-[4px] bg-paper px-3 py-2.5 font-mono text-[12.5px] leading-[1.7]">{{ text }}</pre>
  </section>
</template>

<script setup lang="ts">
import type { TagSource } from '../types/api'
/** sources：定稿時伺服器標的逐 tag 來源。沒有（舊的定稿卡）就退回整段純文字。複製一律複製整段原文。 */
const props = defineProps<{ label: string; text: string; sources?: TagSource[] }>()
const s = useSessionStore()
const copied = ref(false)
async function copy() {
  try { await navigator.clipboard.writeText(props.text); copied.value = true; setTimeout(() => (copied.value = false), 1500) }
  catch { /* 非 https 或權限被拒：使用者可以自己選取 */ }
}

const CHIP = 'inline-block rounded-[3px] border px-1.5 py-px font-mono text-[12px] leading-5 transition-colors'
/** 三種來源的邊框與底色，圖例的小方塊用同一組。rag 用專案的強調色青（焦點、進行中），而且只有它可以點。 */
const SWATCH: Record<TagSource['origin'], string> = {
  rag: 'border-cyan bg-cyan-wash',
  llm: 'border-rule bg-surface',
  base: 'border-rule/60 bg-paper',
}
const TEXT: Record<TagSource['origin'], string> = { rag: 'text-ink', llm: 'text-ink', base: 'text-muted' }
const LEGEND = [
  { origin: 'rag', label: '知識庫片段' },
  { origin: 'llm', label: '模型生成' },
  { origin: 'base', label: '基礎詞' },
] as const
/** 後端多了新的 origin 時當成模型生成，不讓整張卡壞掉。 */
const look = (t: TagSource) => `${SWATCH[t.origin] ?? SWATCH.llm} ${TEXT[t.origin] ?? TEXT.llm}`
</script>
