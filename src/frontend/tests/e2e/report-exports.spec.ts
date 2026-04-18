import { expect, test } from '@playwright/test'
import { createSessionFixture } from './helpers/api'
import { loginViaUi, uniqueEmail } from './helpers/auth'

test.describe('report exports', () => {
  test('export json and markdown trigger downloads', async ({ page }) => {
    const apiBaseUrl = process.env.E2E_API_BASE_URL || 'http://localhost:8080/api'
    const email = uniqueEmail('e2e.export.user')
    const password = 'TestPass123!'

    const fixture = await createSessionFixture(apiBaseUrl, email, password)

    await loginViaUi(page, {
      email: fixture.email,
      password: fixture.password
    })

    await page.goto(`/report/${fixture.sessionId}`)
    await expect(page.getByTestId('report-page')).toBeVisible()

    const jsonDownloadPromise = page.waitForEvent('download')
    await page.getByTestId('export-json-button').click()
    const jsonDownload = await jsonDownloadPromise
    expect(jsonDownload.suggestedFilename().toLowerCase()).toContain('.json')

    const markdownDownloadPromise = page.waitForEvent('download')
    await page.getByTestId('export-markdown-button').click()
    const markdownDownload = await markdownDownloadPromise
    expect(markdownDownload.suggestedFilename().toLowerCase()).toContain('.md')
  })
})
