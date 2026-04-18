import { expect, Page } from '@playwright/test'

export interface UiAuthCredentials {
  email: string
  password: string
  displayName?: string
}

export function uniqueEmail(prefix: string = 'e2e.user'): string {
  const stamp = `${Date.now()}-${Math.floor(Math.random() * 100000)}`
  return `${prefix}+${stamp}@example.com`
}

export async function registerViaUi(page: Page, credentials: UiAuthCredentials): Promise<void> {
  await page.goto('/auth')
  await page.getByTestId('auth-mode-register').click()
  await page.getByTestId('auth-email-input').fill(credentials.email)
  await page.getByTestId('auth-password-input').fill(credentials.password)

  if (credentials.displayName) {
    await page.getByTestId('auth-display-name-input').fill(credentials.displayName)
  }

  await page.getByTestId('auth-submit-button').click()
  await expect(page).not.toHaveURL(/\/auth$/)
  await expect(page.getByTestId('logout-button')).toBeVisible()
}

export async function loginViaUi(page: Page, credentials: UiAuthCredentials): Promise<void> {
  await page.goto('/auth')
  await page.getByTestId('auth-mode-login').click()
  await page.getByTestId('auth-email-input').fill(credentials.email)
  await page.getByTestId('auth-password-input').fill(credentials.password)
  await page.getByTestId('auth-submit-button').click()
  await expect(page).not.toHaveURL(/\/auth$/)
  await expect(page.getByTestId('logout-button')).toBeVisible()
}
