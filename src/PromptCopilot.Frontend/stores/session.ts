import { defineStore } from 'pinia'
import { readSse } from '../lib/sse'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, latestFinalizedTurn as latestFinalizedTurnOf, type ChatState } from '../lib/reducer'
import { loadPersisted, savePersisted } from '../lib/persist'
import { loadPrefs, savePrefs, type Prefs } from '../lib/prefs'
import { composeDraft, appendChip, chipKey, type Chip } from '../lib/composer'
import { latestRecommendableTurn as latestRecommendableTurnOf, adoptPlaceholder } from '../lib/adopt'
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

  /** 對照表正在看的那套；null 表示關閉。 */
  const adoptTarget = ref<{ set: RecommendedSet; dimension: string; turnIndex: number } | null>(null)
  /** 只有最新一張追問卡／定稿卡上的推薦可以採用。 */
  const latestRecommendableTurn = computed<number | null>(() => latestRecommendableTurnOf(state.value.transcript))

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
    busy.value = true
    notice.value = null
    state.value = beginTurn(state.value, display)
    // 送出當下先存一次：輪次中重載時，原文經 draft 回到輸入框（hydrate 會拿掉沒有下文的 user 條目；採用沒有原文可回，存空字串）
    persist({ draft: 'text' in body ? body.text : '' })
    const ctl = new AbortController()
    try {
      const r = await api.openStream(state.value.sessionId!, body, ctl.signal)
      if (r.status === 404) {
        // session 過期：開新的、提示、一般訊息的原文留在輸入框（spec §5）。newSession 會清掉 transcript，所以提示放 notice。
        await newSession()
        notice.value = 'text' in body ? EXPIRED_KEPT_TEXT : '上次的對話已過期，已開新對話。'
        if ('text' in body) { draft.value = body.text; draftDirty.value = true }
        return
      }
      if (!r.ok || !r.body) {
        let msg = r.status === 409 ? '這個對話還有一輪在跑，等它結束再送。' : `送出失敗（HTTP ${r.status}）。`
        // 採用被拒（400／409）帶有理由：直接顯示
        if ('adopt' in body) { try { msg = (await r.json()).error ?? msg } catch { /* 沒 body 就用預設字 */ } }
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

  function openAdopt(set: RecommendedSet, dimension: string, turnIndex: number) {
    if (busy.value || turnIndex !== latestRecommendableTurn.value) return
    adoptTarget.value = { set, dimension, turnIndex }
  }
  function closeAdopt() { adoptTarget.value = null }

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
    dimensionLabels, latestFinalizedTurn,
    prefs, retrievalMismatch, setRetrievalPref, setShowTrace,
    boot, newSession, send, retry, setDraft, toggleChip, isChipSelected, openDrawer, closeDrawer, expandSave, save,
    adoptTarget, latestRecommendableTurn, openAdopt, closeAdopt, adopt,
  }
})
