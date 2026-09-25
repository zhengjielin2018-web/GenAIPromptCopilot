<template>
  <div v-if="t" class="fixed inset-0 z-30 flex items-end justify-center bg-ink/40 p-4 md:items-center" @click.self="s.closeAdopt()">
    <section role="dialog" aria-modal="true" aria-label="採用這套組合" class="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-lg border border-rule bg-surface p-5 shadow-xl">
      <header class="flex items-start gap-4">
        <img v-if="t.set.imageUrl && !imgFailed" :src="t.set.imageUrl" :alt="t.set.title" referrerpolicy="no-referrer"
             class="h-32 w-32 shrink-0 rounded-[4px] object-cover" @error="imgFailed = true">
        <div v-else class="flex h-32 w-32 shrink-0 items-center justify-center rounded-[4px] border border-dashed border-rule text-xs text-muted">無圖</div>
        <div class="min-w-0 flex-1">
          <h2 class="text-base font-bold leading-snug">{{ t.set.title }}</h2>
          <p class="mt-1 text-xs text-muted">{{ notice.text }}</p>
          <button type="button" class="mt-1 text-xs underline underline-offset-2 hover:text-cyan" @click="viewPreset">看完整片段</button>
        </div>
        <button type="button" class="rounded px-1.5 text-sm text-muted hover:text-ink" @click="s.closeAdopt()">關閉</button>
      </header>

      <p class="mt-4 text-xs text-muted">每一項各自決定：留你原本的，還是照這套。沒講過的項目預設照這套。</p>
      <table class="mt-2 w-full text-xs">
        <thead class="text-left text-[11px] text-muted">
          <tr><th class="py-1 pr-2 font-medium">項目</th><th class="py-1 pr-2 font-medium">你的</th><th class="py-1 pr-2 font-medium">這套</th><th class="py-1 font-medium">採用</th></tr>
        </thead>
        <tbody>
          <tr v-for="r in rows" :key="r.facetId" class="border-t border-rule/60 align-top" :class="{ 'opacity-50': !r.available }">
            <td class="py-2 pr-2 font-medium">{{ r.label }}</td>
            <td class="py-2 pr-2 text-muted">{{ mineLabel(r.state) }}</td>
            <td class="py-2 pr-2">
              <span v-if="!r.available" class="text-muted">這套沒有</span>
              <span v-for="tag in r.setTags" :key="tag" class="mr-1 inline-block rounded-[3px] border border-rule bg-paper px-1.5 font-mono text-[11px] leading-5">{{ tag }}</span>
            </td>
            <td class="py-2">
              <span class="inline-flex overflow-hidden rounded-md border border-rule text-[11px]">
                <button type="button" class="px-2 py-1" :class="r.choice === 'mine' ? 'bg-ink text-paper' : 'hover:bg-paper'" :disabled="!r.available"
                        :aria-pressed="r.choice === 'mine'" @click="rows = setChoice(rows, r.facetId, 'mine')">留我的</button>
                <button type="button" class="border-l border-rule px-2 py-1" :class="r.choice === 'set' ? 'bg-ink text-paper' : 'hover:bg-paper'" :disabled="!r.available"
                        :aria-pressed="r.choice === 'set'" @click="rows = setChoice(rows, r.facetId, 'set')">照它的</button>
              </span>
            </td>
          </tr>
        </tbody>
      </table>

      <footer class="mt-4 flex flex-wrap items-center gap-3">
        <button type="button" :disabled="!payload"
                class="rounded-md bg-ink px-3.5 py-1.5 text-sm font-medium text-paper hover:bg-ink/85 disabled:bg-rule disabled:text-muted"
                @click="confirm">確定採用</button>
        <button type="button" class="rounded-md border border-rule px-3 py-1.5 text-sm hover:bg-paper" @click="rows = takeAll(rows)">全部照它的</button>
        <button type="button" class="ml-auto text-sm text-muted hover:text-ink" @click="s.closeAdopt()">取消</button>
      </footer>
    </section>
  </div>
</template>

<script setup lang="ts">
import { adoptRows, setChoice, takeAll, adoptPayload, mineLabel, type AdoptRow } from '../lib/adopt'
import { sourceNotice } from '../lib/copy'
const s = useSessionStore()
const t = computed(() => s.adoptTarget)
const rows = ref<AdoptRow[]>([])
const imgFailed = ref(false)
const notice = computed(() => sourceNotice(t.value?.set.sourceRef))
watch(t, (v) => { rows.value = v ? adoptRows(v.set, s.state.facetStates) : []; imgFailed.value = false }, { immediate: true })
const payload = computed(() => (t.value ? adoptPayload(t.value.set.presetId, t.value.dimension, rows.value) : null))
function confirm() { if (t.value && payload.value) s.adopt(payload.value, t.value.set.title) }
/** 抽屜在對照表下面，先關對照表；id 要在關之前取，關掉後 t 就是 null。 */
function viewPreset() {
  const id = t.value?.set.presetId
  if (id === undefined) return
  s.closeAdopt()
  s.openDrawer(id)
}
function onKey(e: KeyboardEvent) { if (e.key === 'Escape' && t.value) s.closeAdopt() }
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>
