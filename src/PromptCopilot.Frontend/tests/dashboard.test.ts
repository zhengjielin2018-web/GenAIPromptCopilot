import { describe, it, expect } from 'vitest'
import { dashboardRows } from '../lib/dashboard'
import type { FacetCatalog } from '../types/api'

const catalog: FacetCatalog = {
  dimensions: [
    { key: 'style', label: '風格', facets: [{ id: 'style.genre', label: '藝術流派', hint: 'anime' }] },
    { key: 'pose', label: '人物動作', facets: [{ id: 'pose.gaze', label: '視線', hint: 'looking at viewer' }] },
  ],
  profiles: {
    portrait: { labels: { style: '風格', pose: '人物動作' }, dimensions: { style: ['style.genre'], pose: ['pose.gaze'] } },
    vehicle: { labels: { style: '風格', pose: '運動狀態' }, dimensions: { style: ['style.genre'], pose: [] } },
  },
}

describe('dashboardRows', () => {
  it('without a profile lists every dimension as not yet applicable, chips missing', () => {
    const rows = dashboardRows(catalog, null, {}, [])
    expect(rows.map(r => r.applicable)).toEqual([false, false])
    expect(rows[0].chips[0]).toMatchObject({ id: 'style.genre', state: 'missing' })
  })

  it('uses the profile facet list, its labels, and the live states', () => {
    const rows = dashboardRows(catalog, 'vehicle', { 'style.genre': 'covered' }, ['style'])
    expect(rows[0]).toMatchObject({ key: 'style', label: '風格', applicable: true, highlighted: true })
    expect(rows[0].chips[0].state).toBe('covered')
    expect(rows[1]).toMatchObject({ key: 'pose', label: '運動狀態', applicable: false, chips: [] })
  })

  it('renders unknown facet ids with the raw id as label instead of crashing', () => {
    const rows = dashboardRows(catalog, 'portrait', { 'style.mystery': 'waived' }, [])
    const extra = rows[0].chips.find(c => c.id === 'style.mystery')
    expect(extra).toMatchObject({ label: 'style.mystery', state: 'waived' })
  })

  it('defaults a listed facet with no state to missing', () => {
    const rows = dashboardRows(catalog, 'portrait', {}, [])
    expect(rows[1].chips[0].state).toBe('missing')
  })

  it('carries the model-supplied tags onto the chip and null when there are none', () => {
    const rows = dashboardRows(catalog, 'portrait', { 'style.genre': 'covered' }, [], { 'style.genre': 'oil painting' })
    expect(rows[0].chips[0].tags).toBe('oil painting')
    expect(rows[1].chips[0].tags).toBeNull()
  })
})
