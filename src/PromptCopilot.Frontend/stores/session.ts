import { defineStore } from 'pinia'
import { readSse } from '../lib/sse'
import { initialState, beginTurn, applyEvent, endTurn, failHttp, hydrate, latestFinalizedTurn as latestFinalizedTurnOf, type ChatState } from '../lib/reducer'
import { loadPersisted, savePersisted } from '../lib/persist'
import { composeDraft, appendChip, chipKey, type Chip } from '../lib/composer'
import { AGENT_EVENT_TYPES, type AgentEvent, type FacetCatalog } from '../types/api'

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
    const id = await api.createSession()
    state.value = { ...initialState(), sessionId: id }
    chips.value = []; draft.value = ''; draftDirty.value = false
    expandedSaveTurn.value = null; saveState.value = {}; drawerPresetId.value = null
    notice.value = null
    persist()
  }

  async function send() {
    const text = draft.value.trim()
    if (!text || busy.value || !state.value.sessionId) return
    busy.value = true
    draft.value = ''; chips.value = []; draftDirty.value = false
    notice.value = null
    state.value = beginTurn(state.value, text)
    // 送出當下先存一次：輪次中重載時，原文經 draft 回到輸入框（hydrate 會拿掉沒有下文的 user 條目）
    persist({ draft: text })
    const ctl = new AbortController()
    try {
      const r = await api.openStream(state.value.sessionId!, text, ctl.signal)
      if (r.status === 404) {
        // session 過期：開新的、提示、原文留在輸入框（spec §5）。newSession 會清掉 transcript，所以提示放 notice。
        await newSession()
        notice.value = EXPIRED_KEPT_TEXT
        draft.value = text; draftDirty.value = true
        return
      }
      if (!r.ok || !r.body) {
        const msg = r.status === 409 ? '這個對話還有一輪在跑，等它結束再送。' : `送出失敗（HTTP ${r.status}）。`
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
    boot, newSession, send, retry, setDraft, toggleChip, isChipSelected, openDrawer, closeDrawer, expandSave, save,
  }
})
