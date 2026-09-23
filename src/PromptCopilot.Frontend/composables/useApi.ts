import type { FacetCatalog, PresetDetail, SessionSnapshotDto } from '../types/api'

export type SaveResult = { ok: true; id: string } | { ok: false; status: number; error: string }

export function useApi() {
  const base = useRuntimeConfig().public.apiBase as string

  async function createSession(): Promise<string> {
    const r = await fetch(`${base}/api/sessions`, { method: 'POST' })
    if (!r.ok) throw new Error(`createSession HTTP ${r.status}`)
    return (await r.json()).sessionId as string
  }

  /** 404（不存在或已過期）回 null；其他失敗丟例外。 */
  async function getSession(id: string): Promise<SessionSnapshotDto | null> {
    const r = await fetch(`${base}/api/sessions/${encodeURIComponent(id)}`)
    if (r.status === 404) return null
    if (!r.ok) throw new Error(`getSession HTTP ${r.status}`)
    return await r.json()
  }

  async function getFacets(): Promise<FacetCatalog> {
    const r = await fetch(`${base}/api/config/facets`)
    if (!r.ok) throw new Error(`getFacets HTTP ${r.status}`)
    return await r.json()
  }

  async function getPreset(id: number): Promise<PresetDetail | null> {
    const r = await fetch(`${base}/api/presets/${id}`)
    if (r.status === 404) return null
    if (!r.ok) throw new Error(`getPreset HTTP ${r.status}`)
    return await r.json()
  }

  async function saveToShared(id: string, intent: string): Promise<SaveResult> {
    const r = await fetch(`${base}/api/sessions/${encodeURIComponent(id)}/save-to-shared`, {
      method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ intent }),
    })
    if (r.ok) return { ok: true, id: (await r.json()).id }
    let error = `HTTP ${r.status}`
    try { error = (await r.json()).error ?? error } catch { /* 沒 body 就用狀態碼 */ }
    return { ok: false, status: r.status, error }
  }

  /** 不檢查 status：404／409 的處理在 store。 */
  function openStream(id: string, text: string, signal: AbortSignal): Promise<Response> {
    return fetch(`${base}/api/sessions/${encodeURIComponent(id)}/messages`, {
      method: 'POST', headers: { 'content-type': 'application/json', accept: 'text/event-stream' },
      body: JSON.stringify({ text }), signal,
    })
  }

  return { createSession, getSession, getFacets, getPreset, saveToShared, openStream }
}
