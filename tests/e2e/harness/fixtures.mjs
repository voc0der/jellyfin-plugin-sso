import { test as base } from '@playwright/test';
import { stack } from './session.mjs';

// The browser reaches Jellyfin and Authelia by the same names the containers use for each
// other, so every address the plugin builds is one the browser can follow: Chromium resolves
// *.sso.test to the proxy's port on this machine.
export const test = base.extend({
    launchOptions: [async ({ launchOptions }, use) => {
        const args = [...(launchOptions.args ?? []), `--host-resolver-rules=MAP *.sso.test 127.0.0.1:${stack().proxyPort}`];
        await use({ ...launchOptions, args });
    }, { scope: 'worker' }]
});

export { expect } from '@playwright/test';
