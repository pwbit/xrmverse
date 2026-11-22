import { expect, test } from '@playwright/test';
import { localRunEnv } from './harness';

/**
 * Plan 2.8, Task 6: the web admin signs in with the local identity provider (MSAL.js, authorization code with PKCE
 * against <environment>/adfs, client xrmd-web) on a Local-mode host over https (self-signed certificate: ignoreHTTPSErrors).
 */
const env = localRunEnv();
test.use({ baseURL: env.baseURL });

test('The web admin signs in on the local login page and shows the user', async ({ page }) => {
  const microsoft: string[] = [];
  page.on('request', (r) => {
    if (/(^|\.)(login\.microsoftonline\.com|login\.microsoft\.com)$/.test(new URL(r.url()).hostname)) microsoft.push(r.url());
  });

  await page.goto('/manage/');
  await page.getByRole('button', { name: 'Sign in' }).click();

  await page.waitForURL(/\/adfs\/login/);
  await page.getByLabel('User name').fill(env.userName);
  await page.getByLabel('Password').fill(env.password);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await page.waitForURL(/\/manage\//);
  await expect(page.getByRole('banner').getByText(env.userName)).toBeVisible();
  expect(microsoft).toEqual([]);
});
