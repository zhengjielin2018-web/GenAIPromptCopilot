import { describe, it, expect } from 'vitest'
import { readSse } from '../lib/sse'

const enc = new TextEncoder()
function streamOf(chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(c) {
      for (const ch of chunks) c.enqueue(typeof ch === 'string' ? enc.encode(ch) : ch)
      c.close()
    },
  })
}
async function collect(s: ReadableStream<Uint8Array>) {
  const out = []
  for await (const f of readSse(s)) out.push(f)
  return out
}

describe('readSse', () => {
  it('parses one frame', async () => {
    const out = await collect(streamOf(['event: session\ndata: {"a":1}\n\n']))
    expect(out).toEqual([{ event: 'session', data: '{"a":1}' }])
  })

  it('handles two frames in one chunk', async () => {
    const out = await collect(streamOf(['event: a\ndata: 1\n\nevent: b\ndata: 2\n\n']))
    expect(out.map(f => f.event)).toEqual(['a', 'b'])
  })

  it('handles a frame split across chunks mid-line', async () => {
    const out = await collect(streamOf(['event: too', 'l_call\ndata: {"x":', '"y"}\n\n']))
    expect(out).toEqual([{ event: 'tool_call', data: '{"x":"y"}' }])
  })

  it('handles a multibyte character split across chunks', async () => {
    const bytes = enc.encode('event: token\ndata: {"text":"霓虹"}\n\n')
    const cut = 'event: token\ndata: {"text":"'.length + 1   // 「霓」是 3 個位元組，切在第 1 個之後
    const out = await collect(streamOf([bytes.slice(0, cut), bytes.slice(cut)]))
    expect(out).toEqual([{ event: 'token', data: '{"text":"霓虹"}' }])
  })

  it('joins multiple data lines with newline and ignores comments', async () => {
    const out = await collect(streamOf([': keep-alive\nevent: x\ndata: l1\ndata: l2\n\n']))
    expect(out).toEqual([{ event: 'x', data: 'l1\nl2' }])
  })

  it('defaults event to "message" when absent and tolerates CRLF', async () => {
    const out = await collect(streamOf(['data: 1\r\n\r\n']))
    expect(out).toEqual([{ event: 'message', data: '1' }])
  })

  it('emits a trailing frame that has no final blank line', async () => {
    const out = await collect(streamOf(['event: error\ndata: {"code":"x"}']))
    expect(out).toEqual([{ event: 'error', data: '{"code":"x"}' }])
  })
})
