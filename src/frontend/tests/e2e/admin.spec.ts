import { expect, test } from '@playwright/test'
import { loginViaUi, registerViaUi, uniqueEmail } from './helpers/auth'

test.describe('admin access', () => {
  test('non-admin user cannot access admin page', async ({ page }) => {
    const email = uniqueEmail('e2e.admin.user')
    const password = 'TestPass123!'

    await registerViaUi(page, {
      email,
      password,
      displayName: 'E2E Non Admin'
    })

    await page.goto('/admin')
    await expect(page.getByTestId('admin-forbidden')).toBeVisible()
  })

  test('admin user can access admin page and use retention section', async ({ page }) => {
    const adminEmail = process.env.E2E_ADMIN_EMAIL
    const adminPassword = process.env.E2E_ADMIN_PASSWORD

    test.skip(!adminEmail || !adminPassword, 'Set E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD for admin test')

    await loginViaUi(page, {
      email: adminEmail!,
      password: adminPassword!
    })

    await page.goto('/admin')
    await expect(page.getByTestId('admin-page')).toBeVisible()
    await expect(page.getByTestId('retention-status-section')).toBeVisible()

    const runButton = page.getByTestId('run-retention-button')
    await runButton.click()

    const feedback = page.locator('[data-testid="admin-status-message"], [data-testid="admin-error-message"]')
    await expect(feedback.first()).toBeVisible()

    const usersTable = page.getByTestId('admin-users-table')
    await expect(usersTable).toBeVisible()
  })
})
