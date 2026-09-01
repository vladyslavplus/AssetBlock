import { describe, expect, it } from 'vitest'
import {
  ASSET_UPLOAD_MAX_REQUEST_BYTES,
  prepareMultipartUpload,
} from '@/lib/server/multipart-upload'

const boundary = 'assetblock-test-boundary'

function multipartBody(
  parts: ReadonlyArray<{ name: string; value: string; filename?: string }>,
): string {
  const body = parts
    .map(({ name, value, filename }) => {
      const disposition = filename
        ? `form-data; name="${name}"; filename="${filename}"`
        : `form-data; name="${name}"`
      return `--${boundary}\r\nContent-Disposition: ${disposition}\r\n\r\n${value}\r\n`
    })
    .join('')
  return `${body}--${boundary}--\r\n`
}

function uploadRequest(body: string, contentLength = new TextEncoder().encode(body).byteLength) {
  return new Request('http://localhost:3000/api/seller/upload', {
    method: 'POST',
    headers: {
      'Content-Type': `multipart/form-data; boundary=${boundary}`,
      'Content-Length': String(contentLength),
    },
    body,
  })
}

describe('prepareMultipartUpload', () => {
  it('parses bounded metadata and preserves the original body for streaming', async () => {
    const body = multipartBody([
      { name: 'title', value: 'Український asset' },
      { name: 'price', value: '12.50' },
      { name: 'categoryId', value: '123e4567-e89b-12d3-a456-426614174000' },
      { name: 'licenseCode', value: 'PERSONAL' },
      { name: 'tags', value: '3D' },
      { name: 'file', filename: 'asset.zip', value: 'archive-bytes' },
    ])

    const result = await prepareMultipartUpload(uploadRequest(body))

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.fileName).toBe('asset.zip')
    expect(result.metadata.get('title')).toBe('Український asset')
    expect(result.metadata.getAll('tags')).toEqual(['3D'])
    const forward = result.createForwardBody([
      ['title', 'Український asset'],
      ['price', '12.5'],
      ['categoryId', '123e4567-e89b-12d3-a456-426614174000'],
      ['licenseCode', 'PERSONAL'],
      ['tags', '3D'],
    ])
    expect(forward).not.toBeNull()
    if (!forward) return
    const forwardedBody = await new Response(forward.body).text()
    expect(forwardedBody).toContain('name="title"\r\n\r\nУкраїнський asset')
    expect(forwardedBody).toContain('name="price"\r\n\r\n12.5')
    expect(forwardedBody).toContain('filename="asset.zip"\r\n\r\narchive-bytes')
    expect(new TextEncoder().encode(forwardedBody).byteLength).toBe(forward.contentLength)
  })

  it('rejects an oversized request before reading its body', async () => {
    const body = multipartBody([{ name: 'file', filename: 'asset.zip', value: 'bytes' }])
    const result = await prepareMultipartUpload(
      uploadRequest(body, ASSET_UPLOAD_MAX_REQUEST_BYTES + 1),
    )

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.response.status).toBe(413)
    expect((await result.response.json()).code).toBe('ERR_FILE_TOO_LARGE')
  })

  it('rejects uploads without a trustworthy Content-Length', async () => {
    const body = multipartBody([{ name: 'file', filename: 'asset.zip', value: 'bytes' }])
    const request = uploadRequest(body)
    request.headers.delete('content-length')

    const result = await prepareMultipartUpload(request)

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.response.status).toBe(411)
    expect((await result.response.json()).code).toBe('ERR_LENGTH_REQUIRED')
  })

  it('rejects a file part that arrives before bounded metadata', async () => {
    const body = multipartBody([
      { name: 'file', filename: 'asset.zip', value: 'archive-bytes' },
      { name: 'title', value: 'Too late' },
    ])

    const result = await prepareMultipartUpload(uploadRequest(body))

    expect(result.ok).toBe(false)
    if (result.ok) return
    expect(result.response.status).toBe(400)
  })

  it('refuses to reconstruct metadata containing the caller-controlled boundary', async () => {
    const body = multipartBody([
      { name: 'title', value: 'Asset title' },
      { name: 'file', filename: 'asset.zip', value: 'archive-bytes' },
    ])
    const result = await prepareMultipartUpload(uploadRequest(body))

    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.createForwardBody([['title', `unsafe\r\n--${boundary}`]])).toBeNull()
    await result.cancel('test_complete')
  })
})
