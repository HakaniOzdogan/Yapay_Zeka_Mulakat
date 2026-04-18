import { expect, test } from '@playwright/test'
import { registerViaUi, uniqueEmail } from './helpers/auth'

test.describe('sessions list', () => {
  test('logged-in user can open sessions list and delete flow works safely', async ({ page }) => {
    const email = uniqueEmail('e2e.sessions.user')
    const password = 'TestPass123!'

    await registerViaUi(page, {
      email,
      password,
      displayName: 'E2E Sessions'
    })

    await page.goto('/reports')
    await expect(page.getByTestId('sessions-page')).toBeVisible()

    const deleteButtons = page.locator('[data-testid^="session-delete-button-"]')
    const deleteCount = await deleteButtons.count()

    if (deleteCount === 0) {
      await expect(page.getByTestId('sessions-empty-state')).toBeVisible()
      return
    }

    await deleteButtons.first().click()
    await expect(page.getByTestId('delete-confirm-modal')).toBeVisible()

    await page.getByTestId('delete-cancel-button').click()
    await expect(page.getByTestId('delete-confirm-modal')).toBeHidden()

    await deleteButtons.first().click()
    await page.getByTestId('delete-confirm-button').click()

    await expect(page.getByTestId('sessions-success-message')).toContainText('Deleted')
  })
})
