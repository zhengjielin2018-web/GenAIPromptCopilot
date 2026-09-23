<template>
  <div class="rounded-2xl border border-emerald-300 bg-white p-4 shadow-sm dark:border-emerald-800 dark:bg-neutral-900">
    <header class="flex items-center justify-between">
      <h3 class="text-sm font-semibold">定稿</h3>
      <span class="text-[11px] text-neutral-500">第 {{ turnIndex }} 輪</span>
    </header>

    <PromptBlock label="Positive" :text="data.positive" />
    <PromptBlock label="Negative" :text="data.negative" />

    <section v-if="data.tips" class="mt-3">
      <h4 class="text-xs font-medium text-neutral-500">生成建議</h4>
      <p class="mt-1 whitespace-pre-wrap text-sm">{{ data.tips }}</p>
    </section>

    <footer :id="`save-${turnIndex}`" class="mt-4">
      <button v-if="!expanded" type="button" :disabled="save.status === 'saved'"
              class="rounded-md bg-emerald-600 px-3 py-1.5 text-sm text-white hover:bg-emerald-700 disabled:opacity-50"
              @click="s.expandSave(turnIndex)">
        {{ save.status === 'saved' ? '已儲存' : '儲存至共享知識庫' }}
      </button>
      <div v-else class="rounded-md border border-neutral-200 p-3 dark:border-neutral-800">
        <label class="block text-xs text-neutral-500" :for="`intent-${turnIndex}`">一句話描述這張圖（會成為別人檢索到它的依據，可修改）</label>
        <input :id="`intent-${turnIndex}`" v-model="intent" type="text" :disabled="save.status === 'saved' || save.status === 'saving'"
               class="mt-1 w-full rounded border border-neutral-300 bg-white px-2 py-1 text-sm dark:border-neutral-700 dark:bg-neutral-950">
        <div class="mt-2 flex flex-wrap items-center gap-2">
          <button type="button" :disabled="save.status === 'saved' || save.status === 'saving'"
                  class="rounded-md bg-emerald-600 px-3 py-1.5 text-sm text-white hover:bg-emerald-700 disabled:opacity-50"
                  @click="s.save(turnIndex, intent)">
            {{ save.status === 'saving' ? '儲存中…' : save.status === 'saved' ? '已儲存' : '確認儲存' }}
          </button>
          <span v-if="save.status === 'error'" class="text-xs text-red-600">{{ save.error }}</span>
          <span v-if="save.status === 'saved'" class="text-xs text-emerald-700 dark:text-emerald-400">這份定稿已進共享庫，之後的對話可能撈到它當參考。</span>
        </div>
      </div>
    </footer>
  </div>
</template>

<script setup lang="ts">
import type { FinalizedData } from '../types/api'
const props = defineProps<{ data: FinalizedData; turnIndex: number }>()
const s = useSessionStore()
const intent = ref(props.data.intentSummary)
const expanded = computed(() => s.expandedSaveTurn === props.turnIndex)
const save = computed(() => s.saveState[props.turnIndex] ?? { status: 'idle' as const, error: undefined })
</script>
