import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
    testDir: '.',
    testMatch: '*.spec.mjs',
    globalSetup: './harness/global-setup.mjs',

    // One Jellyfin and one Authelia for the whole run, and later tests build on the accounts
    // earlier ones signed in with, so they run in order.
    workers: 1,
    fullyParallel: false,
    retries: 0,
    forbidOnly: !!process.env.CI,

    timeout: 90_000,
    expect: { timeout: 15_000 },

    reporter: process.env.CI
        ? [['list'], ['html', { open: 'never' }]]
        : [['list']],

    use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 900 },
        locale: 'en-US',
        timezoneId: 'UTC',
        // The proxy serves a certificate from the harness's own throwaway CA.
        ignoreHTTPSErrors: true,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure'
    }
});
