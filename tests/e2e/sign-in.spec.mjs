import { expect, test } from './harness/fixtures.mjs';
import {
    admin,
    currentToken,
    currentUserId,
    emptyPasswordSignIn,
    jellyfinUser,
    passwordSignIn,
    quickConnectSignIn,
    signInAsAdmin,
    signInAtAuthelia,
    signInWithPassword,
    signInWithSso,
    stack
} from './harness/session.mjs';
import {
    ADMIN,
    EXPOSED_USER,
    JELLYFIN_URL,
    LOCAL_USER,
    PICTURE_PATH,
    PLUGIN_ID,
    PROVIDER,
    jellyfinLogs,
    restartJellyfin
} from './harness/stack.mjs';

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
    expect(alice.Policy.AuthenticationProviderId).toBe(SSO_PROVIDER_ID);
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

test('the picture Authelia gives becomes the profile picture, and each sign-in replaces it', async ({ page }) => {
    // Alice has signed in above, with the picture her Authelia account carries.
    const { base } = stack();
    const before = await jellyfinUser('alice');
    expect(before.PrimaryImageTag).toBeTruthy();

    // Served as it was stored. Saved without its extension, as 9p4's plugin named pictures,
    // Jellyfin cannot tell what it is and serves it as application/octet-stream.
    const served = await fetch(`${base}/Users/${before.Id}/Images/Primary`);
    expect(served.status).toBe(200);
    expect(served.headers.get('content-type')).toBe('image/png');
    const source = await fetch(`${base}${PICTURE_PATH}`);
    expect(Buffer.from(await served.arrayBuffer()).equals(Buffer.from(await source.arrayBuffer()))).toBe(true);

    await signInWithSso(page, 'alice');

    const after = await jellyfinUser('alice');
    expect(after.PrimaryImageTag).toBeTruthy();
    expect(after.PrimaryImageTag).not.toBe(before.PrimaryImageTag);
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

test('an account made by sign-in cannot sign in with a password', async () => {
    expect(await emptyPasswordSignIn('alice')).toBe(401);
});

test('an account open to an empty password is locked to sign-in when its owner signs in', async ({ page }) => {
    const { exposedUserId } = stack();
    // The state 9p4's plugin left its accounts in on Jellyfin 12.
    expect(await emptyPasswordSignIn(EXPOSED_USER.name)).toBe(200);

    await signInWithSso(page, EXPOSED_USER.name);

    expect(await currentUserId(page)).toBe(exposedUserId);
    expect(await emptyPasswordSignIn(EXPOSED_USER.name)).toBe(401);
    expect((await jellyfinUser(EXPOSED_USER.name)).Policy.AuthenticationProviderId).toBe(SSO_PROVIDER_ID);
});

test('the settings page lists the provider in the dashboard', async ({ page }) => {
    await signInAsAdmin(page);
    await page.evaluate(() => { window.location.hash = '#/configurationpage?name=SSO%20Authentication'; });

    await expect(page.locator('#sso-config-page')).toBeVisible();
    await expect(page.locator('#selectProvider option', { hasText: PROVIDER })).toHaveCount(1);
});

test('an account with a password links its Authelia identity and then signs in with it', async ({ page }) => {
    await signInWithPassword(page, LOCAL_USER);

    await page.goto(`${JELLYFIN_URL}/SSOViews/linking`);
    await expect(page.locator('body')).toContainText(PROVIDER);
    await page.locator(`#sso-provider-list-oid .sso-provider-links-container[data-id="${PROVIDER}"] .sso-provider-add-link`).click();
    await page.locator('#username-textfield').fill('dave');
    await page.locator('#password-textfield').fill('dave-harness-pw');
    await page.locator('#sign-in-button').click();
    await page.waitForURL(/\/web\/(index\.html)?#\/home/, { timeout: 45_000 });

    const { localUserId } = stack();
    const links = await admin('GET', `/sso/oid/links/${localUserId}`);
    expect(links[PROVIDER]).toEqual(['dave']);

    // Dave's identity now opens Erin's account instead of making one of its own.
    await page.context().clearCookies();
    await page.evaluate(() => localStorage.clear());
    await signInWithSso(page, 'dave');
    expect(await currentUserId(page)).toBe(localUserId);
    expect(await jellyfinUser('dave')).toBeUndefined();
});

// Last, because each makes Jellyfin's container again, as an admin changing the variable in a
// Compose file does: same data, new environment.
test.describe('JELLYFIN_ENABLE_PASSWORD_LOGIN', () => {
    test('false refuses every password, right or wrong, and signs no one out', async () => {
        await restartJellyfin({ JELLYFIN_ENABLE_PASSWORD_LOGIN: 'false' });

        expect(await passwordSignIn(ADMIN.name, ADMIN.password)).toBe(403);
        expect(await passwordSignIn(LOCAL_USER.name, LOCAL_USER.password)).toBe(403);
        expect(await passwordSignIn(LOCAL_USER.name, 'not-erins-password')).toBe(403);

        // The plugin refused them, and said why: Jellyfin itself never answers a password 403.
        // Polled, because Jellyfin writes its log in the background.
        await expect.poll(jellyfinLogs).toContain('Password sign-in is off: JELLYFIN_ENABLE_PASSWORD_LOGIN is false');
        await expect.poll(async () => (await jellyfinLogs()).match(/Refused a password sign-in from /g)?.length).toBe(3);

        // The harness's admin session was signed in with a password, before.
        expect((await admin('GET', '/Users/Me')).Name).toBe(ADMIN.name);
    });

    test('false has Jellyfin Web tell someone with a password to ask the administrator', async ({ page }) => {
        await page.goto(`${JELLYFIN_URL}/web/`);
        await page.locator('#txtManualName').fill(LOCAL_USER.name);
        await page.locator('#txtManualPassword').fill(LOCAL_USER.password);
        await page.locator('#txtManualPassword').press('Enter');

        // What Jellyfin Web says for 403. For 401 it would say the password is wrong, which it is not.
        await expect(page.getByText('You are not authorized to access the server at this time')).toBeVisible();
        expect(page.url()).not.toMatch(/#\/home/);
        expect(await currentUserId(page)).toBeNull();
    });

    test('false leaves sign-in through the provider, and Quick Connect approved from it', async ({ page }) => {
        await signInWithSso(page, 'carol');
        const carol = await jellyfinUser('carol');
        expect(await currentUserId(page)).toBe(carol.Id);

        // A TV app, say, approved from the browser that signed in through the provider.
        expect(await quickConnectSignIn(await currentToken(page))).toEqual({ status: 200, userId: carol.Id });
    });

    test('unset again, passwords sign in as before', async () => {
        await restartJellyfin();

        expect(await passwordSignIn(ADMIN.name, ADMIN.password)).toBe(200);
        expect(await passwordSignIn(LOCAL_USER.name, LOCAL_USER.password)).toBe(200);
        expect(await passwordSignIn(LOCAL_USER.name, 'not-erins-password')).toBe(401);
        expect(await jellyfinLogs()).not.toContain('Password sign-in is off');
    });
});
