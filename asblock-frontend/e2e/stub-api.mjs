/**
 * Deterministic local API for Playwright SSR + client catalog fetches.
 * No Stripe, no real credentials, no production traffic.
 */
import http from 'node:http'

const PORT = Number(process.env.PLAYWRIGHT_API_PORT ?? 3999)

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const categoryId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const collectionId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
const authorId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const versionId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'

const license = {
  code: 'PERSONAL',
  displayName: 'Personal use',
  templateVersion: '1',
  terms: 'Personal use only.',
}

const assetListItem = {
  id: assetId,
  title: 'Procedural Shader Kit',
  description: 'A test asset',
  price: 19,
  categoryId,
  categoryName: 'Shaders',
  authorId,
  authorUsername: 'maya',
  createdAt: '2026-01-01T00:00:00.000Z',
  tags: ['unity'],
  averageRating: 5,
}

const shaderSearchItem = {
  ...assetListItem,
  id: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
  title: 'Shader Noise Pack',
  description: 'Filtered search result',
  price: 9,
  averageRating: 4,
}

const assetDetail = {
  ...assetListItem,
  updatedAt: '2026-01-01T00:00:00.000Z',
  currentVersionNumber: 1,
  currentVersionId: versionId,
  currentVersionCreatedAt: '2026-01-01T00:00:00.000Z',
  currentFileName: 'pack.zip',
  currentContentLength: 128,
  currentContentSha256: 'a'.repeat(64),
  currentLicense: license,
}

const collectionDetail = {
  id: collectionId,
  title: 'Starter Collection',
  description: null,
  status: 'PUBLISHED',
  publishedAt: '2026-01-01T00:00:00.000Z',
  archivedAt: null,
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  sellerId: authorId,
  sellerUsername: 'maya',
  items: [
    {
      assetId,
      title: assetListItem.title,
      price: assetListItem.price,
      position: 1,
      isAvailable: true,
      unavailableReason: null,
    },
  ],
}

function send(res, status, body) {
  const json = JSON.stringify(body)
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': 'content-type,authorization',
    'Access-Control-Allow-Methods': 'GET,POST,PUT,PATCH,DELETE,OPTIONS',
  })
  res.end(json)
}

const ACCESS_OK = 'playwright-access-ok'
const sessionUser = {
  id: '11111111-1111-4111-8111-111111111111',
  username: 'seller',
  role: 'User',
  emailVerifiedAt: '2026-01-01T00:00:00.000Z',
  avatarUrl: null,
  bio: null,
  isPublicProfile: true,
  createdAt: '2026-01-01T00:00:00.000Z',
  socialLinks: [],
}
const refreshByToken = Object.create(null)

// Draft-first seller flow state; mutated by the seller endpoints below so the
// smoke test can exercise save → refetch → upload against one workspace.
const workspaceId = '77777777-7777-4777-8777-777777777777'
let workspaceRevision = 1
let declaration = null
let declarationComplete = false

const sellerDetail = {
  id: assetId,
  title: 'Procedural Shader Kit',
  description: 'A test asset',
  price: 19,
  categoryId,
  categoryName: 'Shaders',
  authorId: sessionUser.id,
  authorUsername: 'seller',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  tags: ['unity'],
  latestVersionId: null,
  latestVersionNumber: null,
  currentReadyVersionId: null,
  publicVersionId: null,
  latestProcessingStatus: 'READY',
  latestProcessingUpdatedAt: '2026-01-01T00:00:00.000Z',
  latestProcessingErrorCode: null,
  latestProcessingErrorSummary: null,
}

const draftMaterial = {
  title: 'Procedural Shader Kit',
  description: 'A test asset',
  categoryId,
  tags: ['unity'],
}

function draftSnapshot() {
  return {
    assetId,
    workspaceId,
    workspaceRevision,
    caseRevision: 0,
    material: draftMaterial,
    declaration,
    declarationComplete,
    latestVersionId: null,
    latestVersionNumber: null,
  }
}

function declarationSnapshot() {
  return {
    assetId,
    assetVersionId: null,
    workspaceId,
    workspaceRevision,
    headRevision: workspaceRevision,
    declaration,
    declarationComplete,
  }
}

function bumpRefresh(token, ok) {
  if (!refreshByToken[token]) {
    refreshByToken[token] = { ok: 0, fail: 0 }
  }
  if (ok) refreshByToken[token].ok += 1
  else refreshByToken[token].fail += 1
}

async function readJson(req) {
  const chunks = []
  for await (const chunk of req) {
    chunks.push(chunk)
  }
  if (chunks.length === 0) return {}
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8'))
  } catch {
    return {}
  }
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url ?? '/', `http://127.0.0.1:${PORT}`)
  const pathname = url.pathname

  if (req.method === 'OPTIONS') {
    send(res, 204, {})
    return
  }

  if (pathname === '/health') {
    send(res, 200, { ok: true })
    return
  }

  if (pathname === '/api/payments/capabilities') {
    send(res, 200, { checkoutConfigured: false })
    return
  }

  if (pathname === '/debug/auth') {
    const token = url.searchParams.get('token') ?? ''
    send(res, 200, refreshByToken[token] ?? { ok: 0, fail: 0 })
    return
  }

  if (pathname === '/api/auth/refresh' && req.method === 'POST') {
    void readJson(req).then((body) => {
      const token = typeof body.refreshToken === 'string' ? body.refreshToken : ''
      if (token === 'playwright-refresh') {
        bumpRefresh(token, true)
        const now = Date.now()
        send(res, 200, {
          accessToken: ACCESS_OK,
          refreshToken: 'playwright-refresh',
          accessExpiresAt: new Date(now + 15 * 60 * 1000).toISOString(),
          refreshExpiresAt: new Date(now + 7 * 24 * 60 * 60 * 1000).toISOString(),
        })
        return
      }
      bumpRefresh(token || 'missing', false)
      send(res, 401, { title: 'Unauthorized' })
    })
    return
  }

  if (pathname === '/api/users/me') {
    const auth = req.headers.authorization ?? ''
    if (auth === `Bearer ${ACCESS_OK}`) {
      send(res, 200, sessionUser)
      return
    }
    send(res, 401, { title: 'Unauthorized' })
    return
  }

  if (pathname === `/api/assets/${assetId}`) {
    send(res, 200, assetDetail)
    return
  }

  if (pathname === `/api/assets/${assetId}/versions` && req.method === 'GET') {
    send(res, 200, { items: [], totalCount: 0, page: 1, pageSize: 50 })
    return
  }

  if (pathname === `/api/users/me/assets/${assetId}`) {
    send(res, 200, sellerDetail)
    return
  }

  if (pathname === `/api/users/me/assets/${assetId}/processing-jobs`) {
    send(res, 200, [])
    return
  }

  if (pathname === '/api/assets/drafts' && req.method === 'POST') {
    void readJson(req).then(() => {
      workspaceRevision = 1
      declaration = null
      declarationComplete = false
      send(res, 200, { assetId, workspaceId, workspaceRevision, replayed: false })
    })
    return
  }

  if (pathname === `/api/assets/${assetId}/draft` && req.method === 'GET') {
    send(res, 200, draftSnapshot())
    return
  }

  if (pathname === `/api/assets/${assetId}/draft` && req.method === 'PATCH') {
    void readJson(req).then(() => {
      workspaceRevision += 1
      send(res, 200, {
        workspaceId,
        workspaceRevision,
        headRevision: workspaceRevision,
        replayed: false,
      })
    })
    return
  }

  if (pathname === `/api/assets/${assetId}/declaration` && req.method === 'GET') {
    send(res, 200, declarationSnapshot())
    return
  }

  if (pathname === `/api/assets/${assetId}/declaration` && req.method === 'PUT') {
    void readJson(req).then((body) => {
      workspaceRevision += 1
      declaration = body?.declaration ?? null
      const summary =
        typeof declaration?.ownContributionSummary === 'string'
          ? declaration.ownContributionSummary.trim()
          : ''
      declarationComplete = Boolean(declaration?.redistributionAcknowledged) && summary.length > 0
      send(res, 200, {
        workspaceId,
        workspaceRevision,
        headRevision: workspaceRevision,
        replayed: false,
      })
    })
    return
  }

  if (pathname === `/api/assets/${assetId}/versions` && req.method === 'POST') {
    // Multipart upload — drain the body and acknowledge the created version.
    req.resume()
    req.on('end', () => {
      send(res, 200, { id: versionId, versionNumber: 1, processingStatus: 'PENDING_INSPECTION' })
    })
    return
  }

  if (pathname.startsWith('/api/reviews/')) {
    send(res, 200, { items: [], totalCount: 0, page: 1, pageSize: 50 })
    return
  }

  if (pathname === '/api/assets') {
    const items = url.searchParams.get('search') === 'shader' ? [shaderSearchItem] : [assetListItem]
    send(res, 200, { items, totalCount: items.length, page: 1, pageSize: 12 })
    return
  }

  if (pathname === '/api/categories') {
    send(res, 200, {
      items: [{ id: categoryId, name: 'Shaders', slug: 'shaders', description: null }],
      totalCount: 1,
      page: 1,
      pageSize: 100,
    })
    return
  }

  if (pathname === '/api/tags') {
    send(res, 200, {
      items: [{ id: '11111111-1111-4111-8111-111111111111', name: 'unity' }],
      totalCount: 1,
      page: 1,
      pageSize: 100,
    })
    return
  }

  if (pathname === `/api/collections/${collectionId}`) {
    send(res, 200, collectionDetail)
    return
  }

  if (pathname === '/api/collections') {
    send(res, 200, {
      items: [
        {
          id: collectionId,
          title: 'Starter Collection',
          description: null,
          status: 'PUBLISHED',
          publishedAt: '2026-01-01T00:00:00.000Z',
          createdAt: '2026-01-01T00:00:00.000Z',
          sellerId: authorId,
          sellerUsername: 'maya',
          itemCount: 1,
          coverAssetId: assetId,
          coverAssetTitle: assetListItem.title,
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 12,
    })
    return
  }

  send(res, 404, { title: 'Not found' })
})

server.listen(PORT, '127.0.0.1', () => {
  process.stdout.write(`playwright stub API listening on 127.0.0.1:${PORT}\n`)
})
