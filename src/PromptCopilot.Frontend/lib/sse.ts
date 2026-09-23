export interface SseFrame { event: string; data: string }

/** 把 text/event-stream 切成 frame。只切格式，不解析 JSON；chunk 邊界與多位元組字元由 TextDecoder(stream) 處理。 */
export async function* readSse(stream: ReadableStream<Uint8Array>): AsyncGenerator<SseFrame> {
  const reader = stream.getReader()
  const decoder = new TextDecoder('utf-8')
  let buf = ''
  try {
    while (true) {
      const { value, done } = await reader.read()
      buf += decoder.decode(value ?? new Uint8Array(), { stream: !done })
      buf = buf.replace(/\r\n/g, '\n')
      let idx: number
      while ((idx = buf.indexOf('\n\n')) >= 0) {
        const block = buf.slice(0, idx)
        buf = buf.slice(idx + 2)
        const frame = parseBlock(block)
        if (frame) yield frame
      }
      if (done) break
    }
    const tail = parseBlock(buf)
    if (tail) yield tail
  } finally {
    reader.releaseLock()
  }
}

function parseBlock(block: string): SseFrame | null {
  let event = 'message'
  const data: string[] = []
  for (const line of block.split('\n')) {
    if (!line || line.startsWith(':')) continue
    const colon = line.indexOf(':')
    const field = colon < 0 ? line : line.slice(0, colon)
    let value = colon < 0 ? '' : line.slice(colon + 1)
    if (value.startsWith(' ')) value = value.slice(1)
    if (field === 'event') event = value
    else if (field === 'data') data.push(value)
  }
  if (data.length === 0) return null
  return { event, data: data.join('\n') }
}
