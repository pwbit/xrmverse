import { existsSync } from 'node:fs';
import { defineConfig } from '@playwright/test';

/**
 * The PostgreSQL smoke (plan 2.7b, Task 3, ruling Q10): e2e/postgres-smoke.spec.ts only, against a host on PostgreSQL that
 * is already running (XRMD_E2E_PG_BASE_URL; see the spec). No global setup: it starts no SQL Server and no host.
 */
const preinstalled = '/opt/pw-browsers/chromium';
const executablePath = process.env.XRMD_E2E_CHROMIUM ?? (existsSync(preinstalled) ? preinstalled : undefined);

export default defineConfig({
  testDir: './e2e',
  testMatch: 'postgres-smoke.spec.ts',
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
    ignoreHTTPSErrors: true,
    trace: 'off',
    actionTimeout: 15_000,
    launchOptions: executablePath ? { executablePath } : {},
  },
});
