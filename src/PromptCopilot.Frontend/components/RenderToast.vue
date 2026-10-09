<template>
  <button v-if="s.renderToast" type="button" data-toast="render"
          class="absolute bottom-24 left-1/2 z-20 -translate-x-1/2 rounded-full bg-ink px-4 py-1.5 text-sm font-medium text-paper shadow-md hover:bg-ink/85"
          @click="go">
    {{ s.renderToast.text }}
  </button>
</template>

<script setup lang="ts">
/** 預覽在畫面外完成時的小提示（預覽設計 §8）：點了捲到那張定稿卡，幾秒後自己消失。 */
const s = useSessionStore()
let timer: ReturnType<typeof setTimeout> | null = null
watch(() => s.renderToast, t => {
  if (timer) clearTimeout(timer)
  timer = t ? setTimeout(() => s.dismissRenderToast(), 6000) : null
})
onBeforeUnmount(() => { if (timer) clearTimeout(timer) })
function go() {
  const t = s.renderToast?.turnIndex
  s.dismissRenderToast()
  if (t != null) document.querySelector(`[data-final-turn="${t}"]`)?.scrollIntoView({ behavior: 'smooth', block: 'center' })
}
</script>
