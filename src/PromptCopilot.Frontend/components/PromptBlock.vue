<template>
  <section class="mt-4">
    <div class="flex items-baseline justify-between">
      <h4 class="text-xs font-bold">{{ label }}</h4>
      <button type="button" class="rounded px-1.5 text-xs text-muted hover:text-ink" @click="copy">{{ copied ? '已複製' : '複製' }}</button>
    </div>
    <template v-if="sources?.length">
      <ul class="mt-1.5 flex flex-wrap gap-1">
        <li v-for="(t, i) in sources" :key="i">
          <button v-if="clickable(t)" type="button" :class="[CHIP, look(t), t.origin === 'adopted' ? 'hover:bg-magenta hover:text-paper' : 'hover:bg-cyan hover:text-paper']"
                  :title="chipTitle(t)" @click="s.openDrawer(t.presetIds[0])">
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
import { sourceName } from '../lib/copy'
/** sources：定稿時伺服器標的逐 tag 來源。沒有（舊的定稿卡）就退回整段純文字。複製一律複製整段原文。 */
const props = defineProps<{ label: string; text: string; sources?: TagSource[] }>()
const s = useSessionStore()
const copied = ref(false)
async function copy() {
  try { await navigator.clipboard.writeText(props.text); copied.value = true; setTimeout(() => (copied.value = false), 1500) }
  catch { /* 非 https 或權限被拒：使用者可以自己選取 */ }
}

const CHIP = 'inline-block rounded-[3px] border px-1.5 py-px font-mono text-[12px] leading-5 transition-colors'
/** 四種來源的邊框與底色，圖例的小方塊用同一組。rag 用強調色青、adopted 用洋紅；只有這兩種指向 preset、可以點。 */
const SWATCH: Record<TagSource['origin'], string> = {
  rag: 'border-cyan bg-cyan-wash',
  adopted: 'border-magenta bg-magenta-wash',
  llm: 'border-rule bg-surface',
  base: 'border-rule/60 bg-paper',
}
const TEXT: Record<TagSource['origin'], string> = { rag: 'text-ink', adopted: 'text-ink', llm: 'text-ink', base: 'text-muted' }
const LEGEND = [
  { origin: 'rag', label: '知識庫片段' },
  { origin: 'adopted', label: '採用的組合' },
  { origin: 'llm', label: '模型生成' },
  { origin: 'base', label: '基礎詞' },
] as const
/** 可點的 chip：rag 與 adopted 都指向一筆 preset。 */
const clickable = (t: TagSource) => (t.origin === 'rag' || t.origin === 'adopted') && t.presetIds.length > 0
/** chip 的提示：「來自〈標題〉（Civitai）」／「採用〈標題〉帶進來的」，認得出來源才加括號。 */
function chipTitle(t: TagSource) {
  const name = sourceName(t.sourceRef)
  const who = `〈${t.presetTitle ?? `片段 #${t.presetIds[0]}`}〉${name ? `（${name}）` : ''}`
  return t.origin === 'adopted' ? `採用${who}帶進來的` : `來自${who}`
}
/** 後端多了新的 origin 時當成模型生成，不讓整張卡壞掉。 */
const look = (t: TagSource) => `${SWATCH[t.origin] ?? SWATCH.llm} ${TEXT[t.origin] ?? TEXT.llm}`
</script>
