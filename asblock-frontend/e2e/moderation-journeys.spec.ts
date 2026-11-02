import { expect, test, type Locator, type Page } from '@playwright/test'

const assetId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
const versionId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
const secondVersionId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeef'
const submissionId = '33333333-3333-4333-8333-333333333333'
const appealId = '44444444-4444-4444-8444-444444444444'

async function navigate(page: Page, path: string) {
  await page.goto(path)
  await expect(page).toHaveURL(
    new URL(path, `http://localhost:${process.env.PLAYWRIGHT_PORT ?? 3100}`).href,
  )
}

async function activateByKeyboard(page: Page, control: Locator) {
  await control.focus()
  await page.keyboard.press('Enter')
}

async function prepare(page: Page, state: Record<string, unknown>) {
  const reset = await page.request.post('http://127.0.0.1:3999/__e2e/reset', { data: state })
  expect(reset.ok()).toBeTruthy()
  const role = state.role === 'Moderator' ? 'Moderator' : 'User'
  const accessToken =
    'eyJhbGciOiJIUzI1NiJ9.' +
    Buffer.from(JSON.stringify({ exp: 9999999999, role })).toString('base64url') +
    '.playwright-signature'
  await page.context().addCookies([
    {
      name: 'assetblock_rt',
      value: 'playwright-refresh',
      domain: 'localhost',
      path: '/',
      httpOnly: true,
      sameSite: 'Lax',
    },
    {
      name: 'assetblock_at',
      value: accessToken,
      domain: 'localhost',
      path: '/',
      httpOnly: true,
      sameSite: 'Lax',
    },
  ])
}

test('seller answers the required finding before submitting for human review', async ({ page }) => {
  await prepare(page, { scenario: 'seller-required-response' })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await expect(page.getByRole('heading', { name: /edit listing/i })).toBeVisible()
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await expect(
    page.getByText('Pair verification was disabled by the fixed evaluation policy.', {
      exact: false,
    }),
  ).toBeVisible()
  await expect(
    page.getByText('this does not mean verification succeeded.', { exact: false }),
  ).toBeVisible()

  const submit = page.getByRole('button', { name: 'Submit for review' })
  await expect(submit).toBeDisabled()
  await expect(page.getByText(/required findings still need a response/i)).toBeVisible()
  await page
    .getByRole('textbox', { name: 'Response for src/main.ts' })
    .fill('I wrote this module independently.')
  await activateByKeyboard(page, page.getByRole('button', { name: 'Save response' }))
  await expect(submit).toBeEnabled()
  await activateByKeyboard(page, submit)
  await expect(
    page.getByRole('status').filter({ hasText: 'Submitted for human review.' }),
  ).toBeVisible()
  await expect(page.getByText('Publication: Not published', { exact: true })).toBeVisible()
})

test('seller requests an appeal after rejection and sees its pending state', async ({ page }) => {
  await prepare(page, { scenario: 'seller-appeal', state: 'REJECTED', outcome: 'REJECTED' })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await expect(page.getByRole('heading', { name: 'Request another review' })).toBeVisible()
  await page
    .getByRole('textbox', { name: 'Appeal rationale' })
    .fill('Please have another reviewer assess this decision.')
  const appeal = page.getByRole('button', { name: 'Request another review' })
  await activateByKeyboard(page, appeal)
  await expect(
    page
      .getByRole('status')
      .filter({ hasText: 'Appeal request submitted for independent review.' }),
  ).toBeVisible()
  await expect(page.getByLabel('Appeal status')).toContainText('PENDING REVIEW')
  expect(page.url()).toContain(`/sell/assets/${assetId}/edit`)
})

test('seller withdraws the active review request', async ({ page }) => {
  await prepare(page, {
    scenario: 'seller-withdrawal',
    state: 'IN_REVIEW',
    activeSubmission: true,
    responseRequired: false,
  })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await activateByKeyboard(page, page.getByRole('button', { name: 'Withdraw review request' }))
  await expect(page.getByRole('status').filter({ hasText: /withdrawn/i })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Withdraw review request' })).toHaveCount(0)
})

test('seller sees upheld and approved appeal outcomes', async ({ page }) => {
  await prepare(page, {
    scenario: 'seller-appeal-outcomes',
    state: 'REJECTED',
    outcome: 'REJECTED',
    appealState: 'UPHELD',
  })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await expect(page.getByLabel('Appeal status')).toContainText('UPHELD')

  const approvedReset = await page.request.post('http://127.0.0.1:3999/__e2e/reset', {
    data: {
      scenario: 'seller-appeal-outcomes',
      state: 'REJECTED',
      outcome: 'REJECTED',
      appealState: 'APPROVED',
    },
  })
  expect(approvedReset.ok()).toBeTruthy()
  await page.reload()
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await expect(page.getByLabel('Appeal status')).toContainText('APPROVED')
})

test('seller withdraws an active appeal', async ({ page }) => {
  await prepare(page, {
    scenario: 'seller-appeal-withdrawal',
    state: 'IN_REVIEW',
    kind: 'APPEAL',
    activeSubmission: true,
    appealState: 'PENDING_REVIEW',
    responseRequired: false,
  })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await activateByKeyboard(page, page.getByRole('button', { name: 'Withdraw appeal' }))
  await expect(page.getByRole('status').filter({ hasText: /withdrawn/i })).toBeVisible()
})

test('stub rejects malformed writes and binds appeal withdrawal to case identity', async ({
  request,
}) => {
  const reset = await request.post('http://127.0.0.1:3999/__e2e/reset', {
    data: { scenario: 'seller-required-response' },
  })
  expect(reset.ok()).toBeTruthy()

  const invalidResponse = await request.post(
    `http://127.0.0.1:3999/api/assets/${assetId}/versions/${versionId}/responses`,
    { data: {} },
  )
  expect(invalidResponse.status()).toBe(400)

  const invalidSubmission = await request.post(
    `http://127.0.0.1:3999/api/assets/${assetId}/versions/${versionId}/submissions`,
    {
      data: {
        workspaceId: '77777777-7777-4777-8777-777777777777',
        expectedWorkspaceRevision: 1,
        expectedCaseRevision: 1,
      },
    },
  )
  expect(invalidSubmission.status()).toBe(400)

  const invalidVersionMetadata = await request.post(
    `http://127.0.0.1:3999/api/assets/${assetId}/versions`,
    {
      multipart: {
        licenseCode: 'PERSONAL',
        workspaceId: '77777777-7777-4777-8777-777777777777',
        expectedWorkspaceRevision: '1',
        file: {
          name: 'fixture.zip',
          mimeType: 'application/zip',
          buffer: Buffer.from('invalid-metadata-fixture'),
        },
      },
    },
  )
  expect(invalidVersionMetadata.status()).toBe(400)

  const appealReset = await request.post('http://127.0.0.1:3999/__e2e/reset', {
    data: {
      scenario: 'seller-appeal-withdrawal',
      state: 'IN_REVIEW',
      kind: 'APPEAL',
      activeSubmission: true,
      responseRequired: false,
    },
  })
  expect(appealReset.ok()).toBeTruthy()
  const operationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaac'
  const withdrawBody = { expectedCaseRevision: 1, operationId }
  const wrongSubmission = await request.post(
    `http://127.0.0.1:3999/api/submissions/${submissionId}/withdraw`,
    { data: withdrawBody },
  )
  expect(wrongSubmission.status()).toBe(404)
  expect((await wrongSubmission.json()).code).toBe('ERR_MODERATION_CASE_NOT_FOUND')

  const staleWithdrawal = await request.post(
    `http://127.0.0.1:3999/api/submissions/${appealId}/withdraw`,
    { data: { ...withdrawBody, expectedCaseRevision: 9 } },
  )
  expect(staleWithdrawal.status()).toBe(409)
  expect((await staleWithdrawal.json()).code).toBe('ERR_SUBMISSION_WITHDRAW_CONFLICT')

  const withdrawal = await request.post(
    `http://127.0.0.1:3999/api/submissions/${appealId}/withdraw`,
    { data: withdrawBody },
  )
  expect(withdrawal.status()).toBe(200)
  const replay = await request.post(`http://127.0.0.1:3999/api/submissions/${appealId}/withdraw`, {
    data: withdrawBody,
  })
  expect(replay.status()).toBe(200)
  expect((await replay.json()).replayed).toBe(true)

  const changedPayload = await request.post(
    `http://127.0.0.1:3999/api/submissions/${appealId}/withdraw`,
    { data: { ...withdrawBody, expectedCaseRevision: 2 } },
  )
  expect(changedPayload.status()).toBe(409)
  expect((await changedPayload.json()).code).toBe('ERR_MODERATION_OPERATION_CONFLICT')
})

test('seller sees moderation notifications and can mark one read', async ({ page }) => {
  await prepare(page, { scenario: 'seller-notification' })
  await navigate(page, '/')
  await activateByKeyboard(page, page.getByRole('button', { name: 'Notifications' }))
  await expect(page.getByText('Moderation changes requested')).toBeVisible()
  const open = page.getByRole('link', { name: 'Open' })
  await expect(open).toHaveAttribute('href', `/sell/assets/${assetId}/edit`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'Mark read' }))
  await expect(page.getByRole('button', { name: 'Mark unread' })).toBeVisible()
  await activateByKeyboard(page, open)
  await expect(page).toHaveURL(`/sell/assets/${assetId}/edit`)
})

test('seller sees the approved public version beside a pending newer version', async ({ page }) => {
  await prepare(page, { scenario: 'approved-v1-pending-v2' })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await expect(page.getByRole('heading', { name: 'Version history' })).toBeVisible()
  await expect(page.getByText('pack-v1.zip')).toBeVisible()
  await expect(page.getByText('pack-v2.zip')).toBeVisible()
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await expect(page.getByText('Publication: PUBLISHED')).toBeVisible()
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 2' }))
  await expect(page.getByText('Publication: Not published', { exact: true })).toBeVisible()
  await expect(page.getByText('Submitted for review', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Withdraw review request' })).toBeVisible()
  const history = page
    .getByTestId(`version-checks-${secondVersionId}`)
    .getByRole('list', { name: 'Review history' })
  await expect(history).toContainText('Review request: Submitted')
})

test('seller resubmits a corrected version after changes are requested', async ({ page }) => {
  await prepare(page, {
    scenario: 'seller-correction',
    state: 'CHANGES_REQUESTED',
    outcome: 'CHANGES_REQUESTED',
    responseRequired: false,
  })
  await navigate(page, `/sell/assets/${assetId}/edit`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'View checks for version 1' }))
  await expect(page.getByLabel('Decision reason').first()).toContainText(
    'Please update the source declaration.',
  )
  const submit = page.getByRole('button', { name: 'Submit for review' })
  await expect(submit).toBeEnabled()
  await activateByKeyboard(page, submit)
  await expect(
    page.getByRole('status').filter({ hasText: 'Submitted for human review.' }),
  ).toBeVisible()
})

test('Moderator can claim and release a queued case with keyboard actions', async ({ page }) => {
  await prepare(page, { scenario: 'moderator-claim-release', role: 'Moderator' })
  await navigate(page, '/moderation')
  await expect(page.getByRole('heading', { name: 'Review queue' })).toBeVisible()
  await expect(page.getByText('Procedural Shader Kit')).toBeVisible()
  const claim = page.getByRole('button', { name: 'Claim Procedural Shader Kit' })
  await claim.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('cell', { name: 'You' })).toBeVisible()
  await activateByKeyboard(page, page.getByRole('link', { name: 'Procedural Shader Kit' }))
  await expect(page).toHaveURL(`/moderation/cases/${submissionId}`)
  await expect(page.getByRole('heading', { name: 'Findings and retrieval evidence' })).toBeVisible()
  await expect(page.getByText('Seller response required; unanswered')).toBeVisible()
  await activateByKeyboard(page, page.getByRole('button', { name: 'Release case' }))
  await expect(page.getByRole('button', { name: 'Claim case' })).toBeVisible()
})

test('appeal workspace hides change requests and shows the reviewer exclusion response', async ({
  page,
}) => {
  await prepare(page, {
    scenario: 'appeal-excluded',
    role: 'Moderator',
    kind: 'APPEAL',
    answered: true,
  })
  await navigate(page, '/moderation')
  await expect(page.getByText('Appeal', { exact: true })).toBeVisible()
  await activateByKeyboard(page, page.getByRole('link', { name: 'Procedural Shader Kit' }))
  await expect(page).toHaveURL(`/moderation/cases/${appealId}`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'Claim case' }))
  await expect(page.getByRole('heading', { name: 'Decision and dispositions' })).toBeVisible()
  await expect(page.getByRole('option', { name: 'Request changes' })).toHaveCount(0)
  await expect(
    page.getByRole('alert').filter({ hasText: 'This reviewer is excluded from the appeal case.' }),
  ).toBeVisible()
})

test('stale review keeps the typed decision rationale beside refreshed case state', async ({
  page,
}) => {
  await prepare(page, { scenario: 'stale-review', role: 'Moderator', answered: true })
  await navigate(page, `/moderation/cases/${submissionId}`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'Claim case' }))
  await page
    .getByRole('textbox', { name: 'Decision rationale' })
    .fill('Keep this explanation while I review the changed case.')
  await page.getByRole('combobox', { name: 'Decision' }).selectOption('APPROVE')
  await page
    .getByRole('combobox', { name: 'Disposition for material-source-finding' })
    .selectOption('EXPLAINED')
  await page
    .getByRole('textbox', { name: 'Rationale for material-source-finding' })
    .fill('Evidence supports this disposition.')
  await activateByKeyboard(page, page.getByRole('button', { name: 'Submit decision' }))
  await expect(
    page.getByText(
      'The case was refreshed. Your decision and disposition text remains in the form.',
    ),
  ).toBeVisible()
  await expect(page.getByRole('textbox', { name: 'Decision rationale' })).toHaveValue(
    'Keep this explanation while I review the changed case.',
  )
  await expect(page.getByLabel('Refreshed case state')).toContainText('revision 3')
})

test('moderator cannot open a case owned by the same account', async ({ page }) => {
  await prepare(page, { scenario: 'self-owned-case', role: 'Moderator' })
  await navigate(page, `/moderation/cases/${submissionId}`)
  await expect(page.getByRole('heading', { name: 'Could not load case' })).toBeVisible()
  await expect(
    page
      .getByRole('alert')
      .filter({ hasText: 'You are not permitted to perform this moderation action.' }),
  ).toBeVisible()
  await expect(page.getByRole('button', { name: 'Submit decision' })).toHaveCount(0)
})

test('Moderator completes a bounded coverage assessment', async ({ page }) => {
  await prepare(page, {
    scenario: 'coverage',
    role: 'Moderator',
    answered: true,
  })
  await navigate(page, `/moderation/cases/${submissionId}`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'Claim case' }))
  await expect(page.getByRole('heading', { name: 'Coverage assessment' })).toBeVisible()
  await page
    .getByRole('textbox', { name: 'Competence areas, comma-separated' })
    .fill('licensing, source review')
  await page
    .getByRole('textbox', { name: 'Review plan' })
    .fill('Review retained evidence against the declared contribution.')
  await page
    .getByRole('textbox', { name: 'Rationale', exact: true })
    .fill('The omitted scope needs a documented review.')
  const assess = page.getByRole('button', { name: 'Save assessment' })
  await activateByKeyboard(page, assess)
  await expect(page.getByText('Coverage assessment saved.')).toBeVisible()
})

test('Moderator sees the backend decision blocker code message', async ({ page }) => {
  await prepare(page, {
    scenario: 'decision-blocked',
    role: 'Moderator',
    answered: true,
  })
  await navigate(page, `/moderation/cases/${submissionId}`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'Claim case' }))
  await page.getByRole('combobox', { name: 'Decision' }).selectOption('APPROVE')
  await page
    .getByRole('textbox', { name: 'Decision rationale' })
    .fill('The response evidence is required to resolve the open blocker.')
  await page
    .getByRole('combobox', { name: 'Disposition for material-source-finding' })
    .selectOption('EXPLAINED')
  await page
    .getByRole('textbox', { name: 'Rationale for material-source-finding' })
    .fill('The available evidence supports this disposition.')
  await activateByKeyboard(page, page.getByRole('button', { name: 'Submit decision' }))
  await expect(page.getByLabel('Decision blockers')).toContainText(
    'Seller responses to required findings are outstanding.',
  )
})

test('Moderator records a disposition and approves the claimed case', async ({ page }) => {
  await prepare(page, { scenario: 'moderator-approval', role: 'Moderator', answered: true })
  await navigate(page, `/moderation/cases/${submissionId}`)
  await activateByKeyboard(page, page.getByRole('button', { name: 'Claim case' }))
  await page.getByRole('combobox', { name: 'Decision' }).selectOption('APPROVE')
  await page
    .getByRole('textbox', { name: 'Decision rationale' })
    .fill('The current evidence supports approval.')
  await page
    .getByRole('combobox', { name: 'Disposition for material-source-finding' })
    .selectOption('EXPLAINED')
  await page
    .getByRole('textbox', { name: 'Rationale for material-source-finding' })
    .fill('The seller response explains the match.')
  const submit = page.getByRole('button', { name: 'Submit decision' })
  await activateByKeyboard(page, submit)
  await expect(page.getByText('Decision saved.')).toBeVisible()
  await expect(page.getByText('APPROVED', { exact: true })).toBeVisible()
})
