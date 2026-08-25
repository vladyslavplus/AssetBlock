import { expect, test, type Page } from '@playwright/test'

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const collectionId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd'
const categoryId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const authorId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'

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

const shaderSearchItem = {
  id: 'ffffffff-ffff-4fff-8fff-ffffffffffff',
  title: 'Shader Noise Pack',
  description: 'Filtered search result',
  price: 9,
  categoryId,
  categoryName: 'Shaders',
  authorId,
  authorUsername: 'maya',
  createdAt: '2026-01-01T00:00:00.000Z',
  tags: ['unity'],
  averageRating: 4,
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

function paged<T>(items: T[]) {
  return { items, totalCount: items.length, page: 1, pageSize: 100 }
}

async function interceptBrowserApi(
  page: Page,
  session: typeof sessionUser | null,
  options: { passthroughAuth?: boolean } = {},
) {
  await page.route('**/api/**', async (route) => {
    const url = route.request().url()
    const method = route.request().method()

    if (url.includes('/api/auth/session') || url.includes('/api/auth/refresh')) {
      if (options.passthroughAuth) {
        await route.continue()
        return
      }
    }

    if (url.includes('/api/auth/session')) {
      await route.fulfill({ json: { user: session } })
      return
    }
    if (url.includes('/api/auth/login') && method === 'POST') {
      await route.fulfill({
        json: { ok: true },
        headers: {
          'set-cookie': 'assetblock_rt=playwright-refresh; Path=/; HttpOnly; SameSite=Lax',
        },
      })
      return
    }
    if (url.includes('/api/auth/logout') && method === 'POST') {
      await route.fulfill({ json: { ok: true } })
      return
    }
    if (url.includes('/api/auth/refresh')) {
      await route.fulfill({ json: { ok: true } })
      return
    }
    if (url.includes('/api/seller/upload') && method === 'POST') {
      await route.fulfill({ json: { id: assetId } })
      return
    }
    if (url.includes('/api/payments/checkout')) {
      throw new Error('Stripe checkout must not be requested')
    }
    if (url.includes('/api/seller/listings')) {
      await route.fulfill({ json: paged([]) })
      return
    }
    if (url.includes('/api/categories')) {
      await route.fulfill({
        json: paged([{ id: categoryId, name: 'Shaders', slug: 'shaders', description: null }]),
      })
      return
    }
    if (url.includes('/api/tags')) {
      await route.fulfill({
        json: paged([{ id: '11111111-1111-4111-8111-111111111111', name: 'unity' }]),
      })
      return
    }
    if (url.includes(`/api/collections/${collectionId}`)) {
      await route.fulfill({
        json: {
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
        },
      })
      return
    }
    if (url.includes('/api/collections')) {
      await route.fulfill({
        json: paged([
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
        ]),
      })
      return
    }
    if (url.includes(`/api/assets/${assetId}`)) {
      await route.fulfill({ json: assetListItem })
      return
    }
    if (url.includes('/api/assets')) {
      const search = new URL(url).searchParams.get('search')
      if (search === 'shader') {
        await route.fulfill({ json: paged([shaderSearchItem]) })
        return
      }
      await route.fulfill({ json: paged([assetListItem]) })
      return
    }

    await route.continue()
  })
}

async function seedRefreshCookie(page: Page, value = 'playwright-refresh') {
  await page.context().addCookies([
    {
      name: 'assetblock_rt',
      value,
      domain: '127.0.0.1',
      path: '/',
      httpOnly: true,
      sameSite: 'Lax',
    },
  ])
}

async function authDebug(token: string) {
  const res = await fetch(`http://127.0.0.1:3999/debug/auth?token=${encodeURIComponent(token)}`)
  return (await res.json()) as { ok: number; fail: number }
}

test('public catalog search, asset detail, and collection detail', async ({ page }) => {
  await interceptBrowserApi(page, null)
  await page.goto('/assets')
  await expect(page.getByText('Procedural Shader Kit').first()).toBeVisible()
  const searchRequest = page.waitForRequest((request) => {
    try {
      const requested = new URL(request.url())
      return (
        requested.pathname.includes('/api/assets') &&
        requested.searchParams.get('search') === 'shader'
      )
    } catch {
      return false
    }
  })
  await page.getByRole('textbox', { name: /^search$/i }).fill('shader')
  await searchRequest
  await expect(page.getByRole('heading', { name: 'Shader Noise Pack' }).first()).toBeVisible()
  await expect(page.getByText('Procedural Shader Kit')).toHaveCount(0)
  await page.goto(`/assets/${assetId}`)
  await expect(
    page.getByRole('heading', { level: 1, name: /procedural shader kit/i }),
  ).toBeVisible()
  await page.goto(`/collections/${collectionId}`)
  await expect(page.getByRole('heading', { name: /starter collection/i })).toBeVisible()
})

test('login shows authenticated chrome and logout removes it', async ({ page }) => {
  let signedIn = false
  await page.route('**/api/auth/**', async (route) => {
    const url = route.request().url()
    if (url.includes('/api/auth/session')) {
      await route.fulfill({ json: { user: signedIn ? sessionUser : null } })
      return
    }
    if (url.includes('/api/auth/login') && route.request().method() === 'POST') {
      signedIn = true
      await route.fulfill({
        json: { ok: true },
        headers: {
          'set-cookie': 'assetblock_rt=playwright-refresh; Path=/; HttpOnly; SameSite=Lax',
        },
      })
      return
    }
    if (url.includes('/api/auth/logout') && route.request().method() === 'POST') {
      signedIn = false
      await route.fulfill({ json: { ok: true } })
      return
    }
    await route.continue()
  })
  await page.goto('/login')
  await page.getByRole('textbox', { name: /^email$/i }).fill('seller@example.com')
  await page.getByRole('textbox', { name: /^password$/i }).fill('correct-horse')
  await page.getByRole('button', { name: /^sign in$/i }).click()
  await expect(page.getByRole('button', { name: /^account$/i })).toBeVisible()
  await page.getByRole('button', { name: /^account$/i }).click()
  await page.getByRole('menuitem', { name: /sign out/i }).click()
  await expect(page.getByRole('link', { name: /sign in/i })).toBeVisible()
})

test('silent refresh success keeps the user signed in on a protected page', async ({ page }) => {
  await seedRefreshCookie(page, 'playwright-refresh')
  await interceptBrowserApi(page, null, { passthroughAuth: true })
  await page.goto('/sell')
  await expect(page.getByRole('button', { name: /^account$/i })).toBeVisible()
  await expect(page.getByRole('link', { name: /^sign in$/i })).toHaveCount(0)
  const stats = await authDebug('playwright-refresh')
  expect(stats.ok).toBeGreaterThanOrEqual(1)
  expect(stats.fail).toBe(0)
})

test('failed refresh lands on signed-out UI without a redirect loop', async ({ page }) => {
  let hops = 0
  page.on('framenavigated', () => {
    hops += 1
  })
  await seedRefreshCookie(page, 'playwright-refresh-fail')
  await interceptBrowserApi(page, null, { passthroughAuth: true })
  await page.goto('/sell')
  await expect(page.getByRole('link', { name: /sign in/i }).first()).toBeVisible()
  expect(hops).toBeLessThan(8)
  const stats = await authDebug('playwright-refresh-fail')
  expect(stats.fail).toBeGreaterThanOrEqual(1)
  expect(stats.ok).toBe(0)
})

test('seller upload client validation and successful mocked publish', async ({ page }) => {
  await seedRefreshCookie(page)
  await interceptBrowserApi(page, sessionUser)
  await page.goto('/sell?tab=upload')
  await page.getByRole('button', { name: /upload asset/i }).click()
  await expect(page.getByText(/title is required/i)).toBeVisible()
  await page.getByRole('textbox', { name: /^title$/i }).fill('New Pack')
  await page.getByRole('spinbutton', { name: 'Price in USD' }).fill('11')
  await page.getByLabel(/^category$/i).selectOption({ label: 'Shaders' })
  await page.locator('#upload-file').setInputFiles({
    name: 'pack.zip',
    mimeType: 'application/zip',
    buffer: Buffer.from('zip'),
  })
  await page.getByRole('button', { name: /upload asset/i }).click()
  await expect(page).toHaveURL(/\/sell\?tab=listings/)
})

test('checkout unavailable never navigates to Stripe', async ({ page }) => {
  await interceptBrowserApi(page, sessionUser)
  await page.goto(`/assets/${assetId}`)
  await expect(
    page.getByRole('heading', { level: 1, name: /procedural shader kit/i }),
  ).toBeVisible()
  await expect(
    page.getByText(/checkout unavailable|verify email to buy|sign in to buy/i),
  ).toBeVisible()
})
