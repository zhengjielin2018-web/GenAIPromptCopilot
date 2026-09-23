import type { FacetCatalog, FacetState } from '../types/api'

export interface DashboardChip { id: string; label: string; hint: string; state: FacetState }
export interface DashboardRow { key: string; label: string; applicable: boolean; highlighted: boolean; chips: DashboardChip[] }

/** 儀表板要畫的列。未知 facet id（yaml 與後端不同步）以原 id 當 label，歸到 id 前綴對應的維度。 */
export function dashboardRows(catalog: FacetCatalog, profile: string | null, facetStates: Record<string, FacetState>, highlighted: string[]): DashboardRow[] {
  const byId = new Map(catalog.dimensions.flatMap(d => d.facets.map(f => [f.id, { ...f, dimension: d.key }] as const)))
  const prof = profile ? catalog.profiles[profile] : undefined
  const rows: DashboardRow[] = catalog.dimensions.map(d => {
    const ids = prof ? (prof.dimensions[d.key] ?? []) : d.facets.map(f => f.id)
    const chips: DashboardChip[] = ids.map(id => {
      const f = byId.get(id)
      return { id, label: f?.label ?? id, hint: f?.hint ?? '', state: facetStates[id] ?? 'missing' }
    })
    return { key: d.key, label: prof?.labels[d.key] ?? d.label, applicable: prof ? ids.length > 0 : false, highlighted: highlighted.includes(d.key), chips }
  })
  for (const [id, state] of Object.entries(facetStates)) {
    if (rows.some(r => r.chips.some(c => c.id === id))) continue
    const f = byId.get(id)
    const row = rows.find(r => r.key === (f?.dimension ?? id.split('.')[0]))
    if (row) row.chips.push({ id, label: f?.label ?? id, hint: f?.hint ?? '', state })
  }
  return rows
}
