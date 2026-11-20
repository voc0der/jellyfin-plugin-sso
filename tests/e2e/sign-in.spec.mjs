import { expect, test } from './harness/fixtures.mjs';
import {
    admin,
    currentUserId,
    jellyfinUser,
    signInAsAdmin,
    signInAtAuthelia,
    signInWithSso,
    stack
} from './harness/session.mjs';
import { PLUGIN_ID, PROVIDER } from './harness/stack.mjs';

// Accounts carry this as their authentication provider when sign-in created them: it keeps
// them from signing in with a password.
const SSO_PROVIDER_ID = 'Jellyfin.Plugin.SsoAuthentication.Api.SSOController';

test.describe.configure({ mode: 'serial' });

// Any uncaught error on the plugin's own pages fails the test.
let pageErrors = [];
test.beforeEach(async ({ page }) => {
    pageErrors = [];
    page.on('pageerror', error => {
        if (/\/sso\/|SSOViews|sso-authentication/.test(page.url() + (error.stack || ''))) {
            pageErrors.push(`${page.url()}: ${error.stack || error.message}`);
        }
    });
});
test.afterEach(() => {
    expect(pageErrors, pageErrors.join('\n')).toEqual([]);
});

test('the plugin loads on this Jellyfin', async () => {
    const plugins = await admin('GET', '/Plugins');
    const plugin = plugins.find(entry => entry.Id.replace(/-/g, '') === PLUGIN_ID.replace(/-/g, ''));

    expect(plugin.Status).toBe('Active');
    // Jellyfin lists the plugin under its own Name, which the settings page is loaded by.
    expect(plugin.Name).toBe('SSO Authentication');
    expect(plugin.Version).toMatch(/^1\.\d+\.\d+\.\d+$/);
});

test('a member of the Jellyfin group signs in and lands on the home screen with a new account', async ({ page }) => {
    expect(await jellyfinUser('alice')).toBeUndefined();

    await signInWithSso(page, 'alice');

    const alice = await jellyfinUser('alice');
    expect(alice).toBeDefined();
    expect(await currentUserId(page)).toBe(alice.Id);
    expect(alice.Policy.IsAdministrator).toBe(false);
    // Jellyfin Web's header names the account it signed in.
    await expect(page.locator('.headerUserButton[title="alice"]')).toHaveCount(1);
});

test('signing in again uses the same account', async ({ page }) => {
    const before = await admin('GET', '/Users');
    const alice = before.find(user => user.Name === 'alice');

    await signInWithSso(page, 'alice');

    expect(await currentUserId(page)).toBe(alice.Id);
    expect((await admin('GET', '/Users')).length).toBe(before.length);
});

test('a member of the admins group signs in as an administrator', async ({ page }) => {
    await signInWithSso(page, 'carol');

    const carol = await jellyfinUser('carol');
    expect(await currentUserId(page)).toBe(carol.Id);
    expect(carol.Policy.IsAdministrator).toBe(true);
});

test('someone outside the Jellyfin group is turned away and gets no account', async ({ page }) => {
    await signInAtAuthelia(page, 'mallory');

    await expect(page.locator('body')).toHaveText('Error. Check permissions.');
    expect(page.url()).toContain(`/sso/OID/redirect/${PROVIDER}`);
    expect(await jellyfinUser('mallory')).toBeUndefined();
});

test('the settings page lists the provider in the dashboard', async ({ page }) => {
    await signInAsAdmin(page);
    await page.evaluate(() => { window.location.hash = '#/configurationpage?name=SSO%20Authentication'; });

    await expect(page.locator('#sso-config-page')).toBeVisible();
    await expect(page.locator('#selectProvider option', { hasText: PROVIDER })).toHaveCount(1);
});
