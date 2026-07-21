import { expect, test } from '@playwright/test';

test('sample Upgrade Center Playwright execution', async ({ page }) => {
  await page.goto('about:blank');
  await page.setContent('<main><h1>Upgrade Center Playwright POC</h1></main>');

  await expect(page.getByRole('heading', { name: 'Upgrade Center Playwright POC' })).toBeVisible();

  const heading = await page.locator('h1').innerText();
  console.log(`Sample Playwright script completed successfully: ${heading}`);
});
