import { existsSync } from 'node:fs';
import { defineConfig } from '@playwright/test';

/**
 * End-to-end scenarios against a real xrmd host and SQL Server (see e2e/global-setup.ts). One worker: the
 * scenarios share one server, and some change what the others see (solutions, roles).
 * Chromium: XRMD_E2E_CHROMIUM, else a pre-installed /opt/pw-browsers/chromium, else Playwright's own download.
 */
const preinstalled = '/opt/pw-browsers/chromium';
const executablePath = process.env.XRMD_E2E_CHROMIUM ?? (existsSync(preinstalled) ? preinstalled : undefined);

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  // The PostgreSQL smoke runs against its own host (playwright.postgres.config.ts).
  testIgnore: '**/postgres-smoke.spec.ts',
  globalSetup: './e2e/global-setup.ts',
  globalTeardown: './e2e/global-teardown.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: [['list']],
  outputDir: './e2e/.results',
  use: {
    browserName: 'chromium',
    headless: true,
    // The Local-mode host serves https with a throwaway self-signed certificate (plan 2.8, ruling P3).
    ignoreHTTPSErrors: true,
    trace: 'off',
    actionTimeout: 15_000,
    launchOptions: executablePath ? { executablePath } : {},
  },
});
