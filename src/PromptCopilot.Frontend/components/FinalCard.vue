<template>
  <div data-card="final" class="rounded-md border-2 border-ink bg-surface px-5 py-4">
    <header class="flex items-baseline justify-between">
      <h3 class="text-base font-bold">定稿</h3>
      <span class="text-[11px] tabular-nums text-muted">第 {{ turnIndex }} 輪</span>
    </header>

    <PromptBlock label="正向提示詞" :text="data.positive" :sources="data.positiveSources" />
    <PromptBlock label="負向提示詞" :text="data.negative" :sources="data.negativeSources" />

    <section v-if="data.tips" class="mt-4">
      <h4 class="text-xs font-bold">生成建議</h4>
      <p class="mt-1.5 whitespace-pre-wrap text-sm leading-6 text-ink/90">{{ data.tips }}</p>
    </section>

    <footer :id="`save-${turnIndex}`" class="mt-5 border-t border-rule pt-4">
      <p v-if="superseded" class="text-xs text-muted">這份已被後面的定稿取代。要存進共享知識庫，請用最新那張。</p>
      <button v-else-if="!expanded" type="button" :disabled="save.status === 'saved'"
              class="rounded-md bg-ink px-3.5 py-1.5 text-sm font-medium text-paper hover:bg-ink/85 disabled:bg-rule disabled:text-muted"
              @click="s.expandSave(turnIndex)">
        {{ save.status === 'saved' ? '已存進共享知識庫' : '存進共享知識庫' }}
      </button>
      <div v-else>
        <label class="block text-xs text-muted" :for="`intent-${turnIndex}`">用一句話描述這張圖。別人之後搜尋時會用這句話找到它，可以修改。</label>
        <input :id="`intent-${turnIndex}`" v-model="intent" type="text" :disabled="save.status === 'saved' || save.status === 'saving'"
               class="mt-1.5 w-full rounded-md border border-rule bg-paper px-3 py-1.5 text-sm focus:border-cyan focus:outline-none disabled:text-muted">
        <div class="mt-2.5 flex flex-wrap items-center gap-3">
          <button type="button" :disabled="save.status === 'saved' || save.status === 'saving'"
                  class="rounded-md bg-ink px-3.5 py-1.5 text-sm font-medium text-paper hover:bg-ink/85 disabled:bg-rule disabled:text-muted"
                  @click="s.save(turnIndex, intent)">
            {{ save.status === 'saving' ? '儲存中…' : save.status === 'saved' ? '已存進共享知識庫' : '確認儲存' }}
          </button>
          <span v-if="save.status === 'error'" class="text-xs text-magenta">{{ save.error }}</span>
          <span v-if="save.status === 'saved'" class="text-xs text-cyan">之後的對話可能會拿它當參考。</span>
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
/** 後端 save-to-shared 永遠存最新一次定稿；舊卡已存過的保留「已存」狀態，沒存過的就不給存。 */
const superseded = computed(() => s.latestFinalizedTurn !== props.turnIndex && save.value.status !== 'saved')
</script>
