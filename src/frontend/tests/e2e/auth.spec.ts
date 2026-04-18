import { expect, test } from '@playwright/test'
import { registerViaUi, uniqueEmail } from './helpers/auth'

test.describe('auth', () => {
  test('user can register and gets redirected to authenticated area', async ({ page }) => {
    const email = uniqueEmail('e2e.auth.register')
    const password = 'TestPass123!'

    await registerViaUi(page, {
      email,
      password,
      displayName: 'E2E Register'
    })

    await expect(page).toHaveURL('/')
  })

  test('user can logout and is redirected to /auth', async ({ page }) => {
    const email = uniqueEmail('e2e.auth.logout')
    const password = 'TestPass123!'

    await registerViaUi(page, {
      email,
      password,
      displayName: 'E2E Logout'
    })

    await page.getByTestId('logout-button').click()
    await expect(page).toHaveURL('/auth')
    await expect(page.getByTestId('auth-page')).toBeVisible()
  })

  test('protected route redirects to /auth when unauthenticated', async ({ page }) => {
    await page.goto('/reports')
    await expect(page).toHaveURL('/auth')
    await expect(page.getByTestId('auth-page')).toBeVisible()
  })
})
