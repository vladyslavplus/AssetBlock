import 'server-only'

import { clientClosedRequestResponse, problemResponse } from '@/lib/server/bff-http'
import { ASSET_UPLOAD_MAX_BYTES } from '@/lib/seller/seller-schemas'

const MULTIPART_OVERHEAD_BYTES = 1024 * 1024
const MAX_METADATA_PREFIX_BYTES = 64 * 1024

export const ASSET_UPLOAD_MAX_REQUEST_BYTES = ASSET_UPLOAD_MAX_BYTES + MULTIPART_OVERHEAD_BYTES

interface PreparedMultipartUpload {
  ok: true
  contentType: string
  fileName: string
  metadata: FormData
  createForwardBody: (fields: readonly MultipartField[]) => ForwardMultipartBody | null
  cancel: (reason?: unknown) => Promise<void>
}

export type MultipartField = readonly [name: string, value: string]

interface ForwardMultipartBody {
  body: ReadableStream<Uint8Array>
  contentLength: number
}

interface RejectedMultipartUpload {
  ok: false
  response: Response
}

export type PrepareMultipartUploadResult = PreparedMultipartUpload | RejectedMultipartUpload

function validationProblem(detail: string): RejectedMultipartUpload {
  return {
    ok: false,
    response: problemResponse(400, 'ERR_VALIDATION_FAILED', detail, {
      body: [detail],
    }),
  }
}

function parseBoundary(contentType: string): string | null {
  const match = /(?:^|;)\s*boundary=(?:"([^"]+)"|([^;\s]+))/i.exec(contentType)
  const boundary = match?.[1] ?? match?.[2]
  return boundary && boundary.length <= 70 ? boundary : null
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function combineChunks(chunks: readonly Uint8Array[], totalBytes: number): Uint8Array {
  const combined = new Uint8Array(totalBytes)
  let offset = 0
  for (const chunk of chunks) {
    combined.set(chunk, offset)
    offset += chunk.byteLength
  }
  return combined
}

function encodeMultipartFields(boundary: string, fields: readonly MultipartField[]): Uint8Array {
  const serialized = fields
    .map(
      ([name, value]) =>
        `--${boundary}\r\nContent-Disposition: form-data; name="${name}"\r\n\r\n${value}\r\n`,
    )
    .join('')
  return new TextEncoder().encode(serialized)
}

function forwardedBody(
  prefix: Uint8Array,
  reader: ReadableStreamDefaultReader<Uint8Array>,
): ReadableStream<Uint8Array> {
  let prefixSent = false

  return new ReadableStream<Uint8Array>({
    async pull(controller) {
      if (!prefixSent) {
        prefixSent = true
        controller.enqueue(prefix)
        return
      }

      const next = await reader.read()
      if (next.done) {
        controller.close()
        return
      }
      controller.enqueue(next.value)
    },
    cancel(reason) {
      return reader.cancel(reason)
    },
  })
}

async function parseMetadataPrefix(
  prefix: Uint8Array,
  boundary: string,
  contentType: string,
  filePartStart: number,
): Promise<FormData> {
  const metadataSuffix = new TextEncoder().encode(`\r\n--${boundary}--\r\n`)
  const metadataBytes = new Uint8Array(filePartStart + metadataSuffix.byteLength)
  metadataBytes.set(prefix.subarray(0, filePartStart), 0)
  metadataBytes.set(metadataSuffix, filePartStart)

  const metadataRequest = new Request('http://assetblock.local/upload-metadata', {
    method: 'POST',
    headers: { 'Content-Type': contentType },
    body: metadataBytes,
  })
  return metadataRequest.formData()
}

/**
 * Validates bounded multipart metadata before exposing a one-shot stream for backend forwarding.
 * The browser upload forms place scalar fields before `file`; callers that do not preserve that
 * order are rejected so validation never requires buffering the archive.
 */
export async function prepareMultipartUpload(
  request: Request,
): Promise<PrepareMultipartUploadResult> {
  if (request.signal.aborted) {
    return { ok: false, response: clientClosedRequestResponse() }
  }

  const contentType = request.headers.get('content-type') ?? ''
  if (!contentType.toLowerCase().startsWith('multipart/form-data')) {
    return validationProblem('The request body must be multipart form data.')
  }

  const boundary = parseBoundary(contentType)
  if (!boundary) {
    return validationProblem('The multipart boundary is missing or invalid.')
  }

  const rawContentLength = request.headers.get('content-length')
  const contentLength = rawContentLength ? Number(rawContentLength) : Number.NaN
  if (!Number.isSafeInteger(contentLength) || contentLength <= 0) {
    return {
      ok: false,
      response: problemResponse(
        411,
        'ERR_LENGTH_REQUIRED',
        'A valid Content-Length header is required for archive uploads.',
      ),
    }
  }
  if (contentLength > ASSET_UPLOAD_MAX_REQUEST_BYTES) {
    return {
      ok: false,
      response: problemResponse(
        413,
        'ERR_FILE_TOO_LARGE',
        'The multipart upload exceeds the 250 MiB file limit.',
      ),
    }
  }
  if (!request.body) {
    return validationProblem('Choose a file to upload.')
  }

  const reader = request.body.getReader()
  const chunks: Uint8Array[] = []
  let totalBytes = 0
  const escapedBoundary = escapeRegExp(boundary)
  const filePartPattern = new RegExp(
    `\\r\\n--${escapedBoundary}\\r\\nContent-Disposition:[^\\r\\n]*\\bname="file"`,
    'i',
  )

  try {
    while (totalBytes <= MAX_METADATA_PREFIX_BYTES) {
      const next = await reader.read()
      if (next.done) {
        await reader.cancel()
        return validationProblem('Choose a file to upload.')
      }

      chunks.push(next.value)
      totalBytes += next.value.byteLength
      const prefix = combineChunks(chunks, totalBytes)
      const decoded = new TextDecoder().decode(prefix)
      const filePart = filePartPattern.exec(decoded)

      if (!filePart) {
        continue
      }

      const headerEnd = decoded.indexOf('\r\n\r\n', filePart.index)
      if (headerEnd < 0) {
        continue
      }

      const fileHeaders = decoded.slice(filePart.index, headerEnd)
      const filenameMatch = /(?:^|;)\s*filename="([^"]*)"/i.exec(fileHeaders)
      const fileName = filenameMatch?.[1]?.trim()
      if (!fileName) {
        await reader.cancel()
        return validationProblem('Choose a file to upload.')
      }

      const filePartByteOffset = new TextEncoder().encode(
        decoded.slice(0, filePart.index),
      ).byteLength
      const metadata = await parseMetadataPrefix(prefix, boundary, contentType, filePartByteOffset)
      let bodyCreated = false
      return {
        ok: true,
        contentType,
        fileName,
        metadata,
        createForwardBody(fields) {
          if (bodyCreated) {
            throw new Error('Multipart upload body can only be forwarded once.')
          }
          bodyCreated = true

          const boundaryDelimiter = `\r\n--${boundary}`
          if (fields.some(([, value]) => value.includes(boundaryDelimiter))) {
            return null
          }

          const normalizedMetadata = encodeMultipartFields(boundary, fields)
          const originalFilePartOffset = filePartByteOffset + 2
          const originalFilePrefix = prefix.subarray(originalFilePartOffset)
          const forwardPrefix = combineChunks(
            [normalizedMetadata, originalFilePrefix],
            normalizedMetadata.byteLength + originalFilePrefix.byteLength,
          )

          return {
            body: forwardedBody(forwardPrefix, reader),
            contentLength: contentLength - originalFilePartOffset + normalizedMetadata.byteLength,
          }
        },
        async cancel(reason) {
          await reader.cancel(reason)
        },
      }
    }
  } catch {
    await reader.cancel()
    if (request.signal.aborted) {
      return { ok: false, response: clientClosedRequestResponse() }
    }
    return validationProblem('The multipart request body is malformed.')
  }

  await reader.cancel()
  return validationProblem('Upload metadata must precede the file and stay below 64 KiB.')
}
