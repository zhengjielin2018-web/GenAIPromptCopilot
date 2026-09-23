export interface Chip { dimension: string | null; label: string }

export function chipKey(c: Chip): string { return `${c.dimension ?? ''}\u0000${c.label}` }

function prefix(dimension: string | null, dimLabels: Record<string, string>): string {
  if (dimension === null) return ''
  return `[${dimLabels[dimension] ?? dimension}] `
}

/** 已選 chip → 輸入框文字。同維度用「、」接在前綴後，不同維度各一行；無維度的排最後、不加前綴。 */
export function composeDraft(chips: Chip[], dimLabels: Record<string, string>): string {
  const groups = new Map<string | null, string[]>()
  for (const c of chips) {
    const list = groups.get(c.dimension) ?? []
    list.push(c.label)
    groups.set(c.dimension, list)
  }
  const lines: string[] = []
  for (const [dim, labels] of groups) if (dim !== null) lines.push(prefix(dim, dimLabels) + labels.join('、'))
  const free = groups.get(null)
  if (free) lines.push(free.join('、'))
  return lines.join('\n')
}

/** 使用者手動改過草稿之後，chip 只做附加，不重組。 */
export function appendChip(draft: string, chip: Chip, dimLabels: Record<string, string>): string {
  const line = prefix(chip.dimension, dimLabels) + chip.label
  return draft ? `${draft}\n${line}` : line
}
