import { defineStore } from 'pinia'
import { readSse } from '../lib/sse'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, appendBatch, latestFinalizedTurn as latestFinalizedTurnOf, type ChatState, type FinalEntry } from '../lib/reducer'
import { loadPersisted, savePersisted } from '../lib/persist'
import { loadPrefs, savePrefs, type Prefs } from '../lib/prefs'
import { composeDraft, appendChip, chipKey, type Chip } from '../lib/composer'
import { adoptPlaceholder } from '../lib/adopt'
import { pendingConfirmTurn, confirmDisplay } from '../lib/confirm'
import { messageBody } from '../lib/safety'
import { AGENT_EVENT_TYPES, type AdoptRequest, type AgentEvent, type FacetCatalog, type RecommendedSet, type RetrievalMode } from '../types/api'
import type { TurnBody } from '../composables/useApi'

type SaveStatus = { status: 'idle' | 'saving' | 'saved' | 'error'; error?: string }

const BACKEND_DOWN = '連不到後端。請確認 API 在跑，再按「重試」。'
const EXPIRED_KEPT_TEXT = '上次的對話已過期，已開新對話。原文留在輸入框，可以直接再送。'

/** 唯一的 store。動作只做 I/O；對話狀態的變更全部走 lib/reducer 的純函式。 */
export const useSessionStore = defineStore('session', () => {
  const api = useApi()

  const state = ref<ChatState>(initialState())
  const catalog = ref<FacetCatalog | null>(null)
  const bootError = ref<string | null>(null)
  const notice = ref<string | null>(null)
  const busy = ref(false)

  /** 跨對話的偏好。retrieval 只在 newSession 時送出；showTrace 純顯示。 */
  const prefs = ref<Prefs>(loadPrefs())
  function setRetrievalPref(v: RetrievalMode) { prefs.value = { ...prefs.value, retrieval: v }; savePrefs(prefs.value) }
  function setShowTrace(v: boolean) { prefs.value = { ...prefs.value, showTrace: v }; savePrefs(prefs.value) }
  /** 開關值與目前這段對話的模式不同：畫面要說「新對話後生效」。
   *  還沒有對話時 state.retrieval 只是預設值，不算不一致，免得空白頁就冒出提示。 */
  const retrievalMismatch = computed(() => !!state.value.sessionId && prefs.value.retrieval !== state.value.retrieval)

  /** 測試用的審查開關：後端開放才顯示；下一則訊息就生效。刻意不存：重新整理就回到審查開著，免得忘了關回來。 */
  const safetyCanDisable = ref(false)
  const safetyOff = ref(false)
  function setSafetyOff(v: boolean) { safetyOff.value = v }

  const draft = ref('')
  const chips = ref<Chip[]>([])
  const draftDirty = ref(false)

  const drawerPresetId = ref<number | null>(null)
  const expandedSaveTurn = ref<number | null>(null)
  const saveState = ref<Record<number, SaveStatus>>({})

  /** 維度 key → 中文名。題材有自己的叫法時（載具的 pose 叫「運動狀態」）以題材為準。 */
  const dimensionLabels = computed<Record<string, string>>(() => {
    const c = catalog.value
    if (!c) return {}
    const base = Object.fromEntries(c.dimensions.map(d => [d.key, d.label]))
    const p = state.value.profile ? c.profiles[state.value.profile] : null
    return { ...base, ...(p?.labels ?? {}) }
  })

  /** 最新一張定稿卡：save_consent_requested 要展開它，也只有它可以存（後端永遠存 LastFinal）。 */
  const latestFinalizedTurn = computed<number | null>(() => latestFinalizedTurnOf(state.value.transcript))

  /** 伺服器回過 409 的確認卡輪次（跟 batchState 的 stale 同一個想法）。不存：重載後由 transcript 重新推導；可 JSON 來回。 */
  const staleConfirms = ref<number[]>([])

  /** 可以按的確認卡（先確認再動手設計 §7）；null 表示沒有。輸入框的提示與確認卡的按鈕看它。 */
  const pendingConfirm = computed<number | null>(() => pendingConfirmTurn(state.value.transcript, staleConfirms.value))

  /** 對照表正在看的那套；null 表示關閉。 */
  const adoptTarget = ref<{ set: RecommendedSet; dimension: string; turnIndex: number } | null>(null)

  /** 換一批的狀態，key 是 `${turnIndex}:${dimension}`。不存：重載後回到可以再按。
   *  stale：伺服器回 409（這張卡已經不是最新，或這段對話還有一輪在跑）——不可能靠再按解決，畫面不給重試。 */
  const batchState = ref<Record<string, 'loading' | 'error' | 'exhausted' | 'stale'>>({})

  /** opts.draft 只在送出當下帶原文（輪次中重載時放回輸入框），其餘時候存空字串。 */
  function persist(opts: { draft?: string } = {}) {
    const id = state.value.sessionId
    if (!id) return
    // 已存過的輪次跟著存：重載後定稿卡要維持「已存」，否則會再存一筆重複的進共享庫
    const savedTurns = Object.entries(saveState.value).filter(([, v]) => v.status === 'saved').map(([k]) => Number(k))
    savePersisted({ sessionId: id, transcript: state.value.transcript, savedTurns, draft: opts.draft ?? '' })
  }

  /** 輪次中重載：送出的原文放回輸入框，不自動送。 */
  function restoreDraft(text: string | undefined) {
    if (!text) return
    draft.value = text
    draftDirty.value = true
  }

  /** 開頁：載 catalog；有舊 session 就用 GET 拿權威狀態 + 本地 transcript 重建，404 就開新的。 */
  async function boot() {
    bootError.value = null
    try {
      catalog.value = await api.getFacets()
      safetyCanDisable.value = await api.getSafetyConfig()
      const saved = loadPersisted()
      if (saved) {
        const dto = await api.getSession(saved.sessionId)
        if (dto) {
          state.value = hydrate(initialState(), dto, saved.transcript)
          for (const t of saved.savedTurns ?? []) saveState.value[t] = { status: 'saved' }
          restoreDraft(saved.draft)
          return
        }
        await newSession()
        notice.value = saved.draft ? EXPIRED_KEPT_TEXT : '上次的對話已過期，已開新對話。'
        restoreDraft(saved.draft)
        return
      }
      await newSession()
    } catch {
      bootError.value = BACKEND_DOWN
    }
  }

  /** 不先清 sessionStorage：createSession 失敗時舊對話要留著，重載還回得去。成功後最後的 persist() 會蓋掉舊的。 */
  async function newSession() {
    const created = await api.createSession(prefs.value.retrieval)
    state.value = { ...initialState(), sessionId: created.sessionId, retrieval: created.retrieval }
    chips.value = []; draft.value = ''; draftDirty.value = false
    expandedSaveTurn.value = null; saveState.value = {}; drawerPresetId.value = null
    // 舊對話的組合不能採用到新對話
    adoptTarget.value = null
    batchState.value = {}
    staleConfirms.value = []
    notice.value = null
    persist()
  }

  async function send() {
    const text = draft.value.trim()
    // busy／沒 session 時先擋：否則輸入框被清掉、runTurn 又不送，原文就丟了
    if (!text || busy.value || !state.value.sessionId) return
    draft.value = ''; chips.value = []; draftDirty.value = false
    await runTurn(text, { text })
  }

  /** 一輪：display 是使用者泡泡先顯示的字（採用時是暫代字，session 事件會換成伺服器組的那句），body 是真正送出的內容。 */
  async function runTurn(display: string, body: TurnBody) {
    if (busy.value || !state.value.sessionId) return
    // 另一輪開始後，還開著的對照表不能再採用（它對應的是舊狀態）
    closeAdopt()
    busy.value = true
    notice.value = null
    state.value = beginTurn(state.value, display, { confirm: 'confirm' in body })
    // 送出當下先存一次：輪次中重載時，原文經 draft 回到輸入框（hydrate 會拿掉沒有下文的 user 條目；採用沒有原文可回，存空字串）
    persist({ draft: 'text' in body ? body.text : '' })
    const ctl = new AbortController()
    try {
      const r = await api.openStream(state.value.sessionId!, messageBody(body, { canDisable: safetyCanDisable.value, off: safetyOff.value }), ctl.signal)
      if (r.status === 404) {
        // session 過期：開新的、提示、一般訊息的原文留在輸入框（spec §5）。newSession 會清掉 transcript，所以提示放 notice。
        await newSession()
        notice.value = 'text' in body ? EXPIRED_KEPT_TEXT : '上次的對話已過期，已開新對話。'
        if ('text' in body) { draft.value = body.text; draftDirty.value = true }
        return
      }
      if (!r.ok || !r.body) {
        let msg = r.status === 409 ? '這個對話還有一輪在跑，等它結束再送。' : `送出失敗（HTTP ${r.status}）。`
        // 採用、按確認被拒（400／409）與審查開關被拒（403：後端中途關掉了開放）帶有理由：直接顯示
        if ('adopt' in body || 'confirm' in body || r.status === 403) { try { msg = (await r.json()).error ?? msg } catch { /* 沒 body 就用預設字 */ } }
        // 按確認被 409 擋下：這張卡不會因為再按而成功，標成過期，按鈕與輸入框提示一起收掉
        if ('confirm' in body && r.status === 409 && !staleConfirms.value.includes(body.confirm.turnIndex))
          staleConfirms.value = [...staleConfirms.value, body.confirm.turnIndex]
        state.value = failHttp(state.value, `http_${r.status}`, msg)
        return
      }
      for await (const frame of readSse(r.body)) {
        if (!(AGENT_EVENT_TYPES as readonly string[]).includes(frame.event)) { console.warn('unknown SSE event', frame.event); continue }
        let data: unknown
        try { data = JSON.parse(frame.data) } catch { console.warn('bad SSE data', frame.event, frame.data); continue }
        state.value = applyEvent(state.value, { ...(data as object), type: frame.event } as AgentEvent)
      }
    } catch (e) {
      console.warn('stream aborted', e)
    } finally {
      state.value = endTurn(state.value)
      persist()
      busy.value = false
    }
  }

  /** 按確認卡（先確認再動手設計 §3.5）：泡泡先顯示「對，就這樣」或選的那句，session 事件帶回同一句。只有可按的那張、沒在跑時能按（伺服器也會擋）。 */
  async function confirm(turnIndex: number, choice: number | null) {
    if (busy.value || turnIndex !== pendingConfirm.value) return
    const entry = state.value.transcript.findLast((e): e is FinalEntry => e.kind === 'final' && e.turnIndex === turnIndex)
    if (!entry || entry.data.kind !== 'confirm') return
    await runTurn(confirmDisplay(entry.data, choice), { confirm: { turnIndex, choice } })
  }

  function openAdopt(set: RecommendedSet, dimension: string, turnIndex: number) {
    if (busy.value || turnIndex !== latestFinalizedTurn.value) return
    adoptTarget.value = { set, dimension, turnIndex }
  }
  function closeAdopt() { adoptTarget.value = null }

  /** 換一批：只有最新一張卡、沒在跑回合時能按（伺服器也會擋）。成功就接在那一排右邊並存檔；空批代表這個維度沒有更多了。 */
  async function nextBatch(turnIndex: number, dimension: string) {
    const key = `${turnIndex}:${dimension}`
    const id = state.value.sessionId
    if (!id || busy.value || turnIndex !== latestFinalizedTurn.value || batchState.value[key] === 'loading') return
    batchState.value = { ...batchState.value, [key]: 'loading' }
    try {
      const r = await api.nextRecommendations(id, dimension, turnIndex)
      // 舊對話的回應不能接到新對話：等待期間換了 session 就整批放棄，不動 state／batchState、也不 persist
      if (state.value.sessionId !== id) return
      if (!r.ok) {
        // 404：session 過期，跟 runTurn 一樣開新對話、提示（換一批沒有原文要留，不用 EXPIRED_KEPT_TEXT）
        if (r.status === 404) { await newSession(); notice.value = '上次的對話已過期，已開新對話。'; return }
        // 409：這張卡已經不是最新、或這段對話還有一輪在跑——不是暫時性的，再按也不會成功，不給重試
        if (r.status === 409) { batchState.value = { ...batchState.value, [key]: 'stale' }; return }
        // 503／網路錯誤：可能是暫時的，維持舊文案讓使用者再按一次
        batchState.value = { ...batchState.value, [key]: 'error' }
        return
      }
      if (r.row.sets.length === 0) { batchState.value = { ...batchState.value, [key]: 'exhausted' }; return }
      state.value = appendBatch(state.value, turnIndex, r.row)
      const { [key]: _done, ...rest } = batchState.value
      batchState.value = rest
      persist()
    } catch {
      if (state.value.sessionId !== id) return
      batchState.value = { ...batchState.value, [key]: 'error' }
    }
  }

  /** 確定採用：關對照表、走一般的一輪。泡泡先顯示「採用〈標題〉…」。 */
  async function adopt(req: AdoptRequest, title: string) {
    closeAdopt()
    await runTurn(adoptPlaceholder(title), { adopt: req })
  }

  /** 失敗條目的「重試」：原文填回輸入框，不自動送。 */
  function retry(originalText: string) {
    chips.value = []
    draft.value = originalText
    draftDirty.value = true
  }

  function setDraft(text: string) {
    draft.value = text
    draftDirty.value = true
  }

  function toggleChip(chip: Chip) {
    if (draftDirty.value) { draft.value = appendChip(draft.value, chip, dimensionLabels.value); return }
    const key = chipKey(chip)
    const i = chips.value.findIndex(c => chipKey(c) === key)
    if (i >= 0) chips.value.splice(i, 1); else chips.value.push(chip)
    draft.value = composeDraft(chips.value, dimensionLabels.value)
  }

  function isChipSelected(chip: Chip) { return chips.value.some(c => chipKey(c) === chipKey(chip)) }

  function openDrawer(id: number) { drawerPresetId.value = id }
  function closeDrawer() { drawerPresetId.value = null }

  function expandSave(turnIndex: number) { expandedSaveTurn.value = turnIndex }

  async function save(turnIndex: number, intent: string) {
    const id = state.value.sessionId
    const text = intent.trim()
    if (!id || !text) { saveState.value[turnIndex] = { status: 'error', error: '描述不可為空' }; return }
    // 後端存的是最新一次定稿；從舊卡存會把舊描述配上新提示詞
    if (turnIndex !== latestFinalizedTurn.value) { saveState.value[turnIndex] = { status: 'error', error: '這份定稿已被後面的定稿取代，只能存最新的那一份。' }; return }
    saveState.value[turnIndex] = { status: 'saving' }
    try {
      const r = await api.saveToShared(id, text)
      saveState.value[turnIndex] = r.ok ? { status: 'saved' } : { status: 'error', error: r.error }
      if (r.ok) persist()
    } catch {
      saveState.value[turnIndex] = { status: 'error', error: BACKEND_DOWN }
    }
  }

  return {
    state, catalog, bootError, notice, busy, draft, chips, draftDirty, drawerPresetId, expandedSaveTurn, saveState,
    dimensionLabels, latestFinalizedTurn, pendingConfirm, confirm,
    prefs, retrievalMismatch, setRetrievalPref, setShowTrace, safetyCanDisable, safetyOff, setSafetyOff,
    boot, newSession, send, retry, setDraft, toggleChip, isChipSelected, openDrawer, closeDrawer, expandSave, save,
    adoptTarget, openAdopt, closeAdopt, adopt,
    batchState, nextBatch,
  }
})
