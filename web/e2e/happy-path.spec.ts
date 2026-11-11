import type { Page } from '@playwright/test'
import { expect, test } from '@playwright/test'

// The whole to-be flow through the browser, against the real API, database, outbox and agent.
// The agent runs its scripted model here, so this proves the plumbing, not the language model.

async function signInAs(page: Page, name: string) {
  const switchUser = page.getByRole('button', { name: 'Switch user' })
  if (await switchUser.isVisible().catch(() => false)) await switchUser.click()
  await page.getByRole('button', { name: new RegExp(name) }).click()
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible()
}

async function pick(page: Page, scope: ReturnType<Page['getByTestId']>, label: RegExp, search: string) {
  const box = scope.getByLabel(label)
  await box.fill(search)
  await scope.getByRole('option').filter({ hasText: search }).first().getByRole('button').click()
  await expect(box).toHaveValue(search)
}

test('requestor describes a need, reviewer approves, PRs are created (simulated) and audited', async ({ page }) => {
  const title = `E2E laptops ${Date.now()}`
  const delivery = new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10)

  await page.goto('/')
  await signInAs(page, 'Rina')
  await expect(page.getByText('SAP is simulated.')).toBeVisible()

  // ---- Requestor: describe the need to the assistant -------------------------------------------
  await page.getByRole('button', { name: 'New request' }).click()
  await page.getByLabel(/^Title/).fill(title)
  await page.getByLabel(/^Section/).selectOption({ label: 'IT Infrastructure' })
  await page.getByLabel(/^Plant/).selectOption('1100')

  await page.getByLabel('Message to the PR assistant').fill('I need 5 laptops for new engineers')
  await page.getByRole('button', { name: 'Send' }).click()
  await expect(page.getByText('Suggested form fill')).toBeVisible({ timeout: 30_000 })
  await page.getByRole('button', { name: 'Apply to form' }).click()

  // The assistant found the material group in SAP; the rules engine locked the SAP fields.
  const line1 = page.getByTestId('line-1')
  await expect(line1.getByLabel(/^Material group/)).toHaveValue('IT01')
  await expect(line1.getByLabel(/^Quantity/)).toHaveValue('5')
  await expect(line1.getByText('Rule R01')).toBeVisible()
  await expect(line1.getByText('Placeholder rule')).toBeVisible()
  await expect(line1.locator('dl')).toContainText('NB')
  await expect(line1.locator('dl')).toContainText('61100000')
  await expect(line1.locator('dl')).toContainText('P01')

  // The requestor supplies what only they know.
  await line1.getByLabel(/^Unit price/).fill('15000000')
  await line1.getByLabel(/^Delivery date/).fill(delivery)
  await pick(page, line1, /^Cost center/, 'CC1101')
  await expect(line1.getByText('Complete')).toBeVisible()

  // A service on the same request forces a second SAP PR.
  await page.getByRole('button', { name: 'Add line' }).click()
  const line2 = page.getByTestId('line-2')
  await line2.getByLabel(/^What are you buying/).fill('Laptop imaging and setup')
  await line2.getByLabel(/^Item type/).selectOption('Service')
  await line2.getByLabel(/^Material group/).selectOption('IT10')
  await line2.getByLabel(/^Quantity/).fill('1')
  await line2.getByLabel(/^Unit(?! price)/).selectOption('AU')
  await line2.getByLabel(/^Unit price/).fill('5000000')
  await expect(line2.locator('dl')).toContainText('D') // item category for services
  await expect(line2.getByText('Complete')).toBeVisible()

  await expect(page.getByText('This becomes 2 SAP PRs')).toBeVisible()
  await expect(page.getByText('Within budget')).toBeVisible()

  await page.getByRole('button', { name: 'Submit for review' }).click()
  await expect(page.getByText('Waiting for review').first()).toBeVisible()
  await expect(page.getByRole('heading', { name: title })).toBeVisible()

  // ---- Reviewer: approve ----------------------------------------------------------------------
  await signInAs(page, 'Budi')
  await page.getByRole('link', { name: /Review queue/ }).click()
  await page.getByRole('row', { name: new RegExp(title) }).getByRole('link').click()
  await expect(page.getByText('This becomes 2 SAP PRs')).toBeVisible()
  await page.getByLabel(/^Comment/).fill('Approved for the January joiners')
  await page.getByRole('button', { name: 'Approve and create in SAP' }).click()

  // The outbox creates both PRs; the page follows along.
  await expect(page.getByText('Created in SAP').first()).toBeVisible({ timeout: 30_000 })
  const prs = page.getByRole('listitem').filter({ hasText: /PR 90\d{8}/ })
  await expect(prs).toHaveCount(2)
  await expect(prs.first()).toContainText('SIMULATED')
  await expect(page.getByText('SIMULATED, not in SAP.')).toBeVisible()
  for (const step of ['Draft created', 'Submitted for review', 'Approved', 'PR created in SAP', 'All PRs created in SAP'])
    await expect(page.getByRole('listitem').filter({ hasText: step }).first()).toBeVisible()

  // ---- Admin: the job monitor and the audit trail ---------------------------------------------
  await signInAs(page, 'Sari')
  await page.getByRole('link', { name: 'Administration' }).click()

  await page.getByRole('tab', { name: 'Jobs' }).click()
  const createJob = page.getByRole('row').filter({ hasText: 'Create PR(s) in SAP' }).first()
  await expect(createJob).toContainText('Done')
  await expect(page.getByRole('row').filter({ hasText: 'SIMULATED PR 90' }).first()).toContainText('Done') // the "created" email

  await page.getByRole('tab', { name: 'Audit' }).click()
  for (const action of ['request.created_in_sap', 'sap.pr_created', 'request.approved', 'request.submitted'])
    await expect(page.getByRole('cell', { name: action, exact: true }).first()).toBeVisible()

  await page.getByRole('tab', { name: 'SAP' }).click()
  await expect(page.getByText(/Simulated PRs \(\d+\)/)).toBeVisible()
})
