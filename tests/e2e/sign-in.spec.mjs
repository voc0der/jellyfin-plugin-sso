import { expect, test } from './harness/fixtures.mjs';
import {
    admin,
    currentToken,
    currentUserId,
    emptyPasswordSignIn,
    jellyfinUser,
    passwordSignIn,
    quickConnectSignIn,
    restartJellyfin,
    signInAsAdmin,
    signInAtAuthelia,
    signInWithPassword,
    signInWithSso,
    stack
} from './harness/session.mjs';
import {
    ADMIN,
    AUTH_URL,
    EXPOSED_USER,
    JELLYFIN_URL,
    LOCAL_USER,
    PICTURE_PATH,
    PLUGIN_ID,
    PROVIDER,
    jellyfinLogs
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

test.describe('automatic sign-in', () => {
    let original;
    const configurationPath = `/Plugins/${PLUGIN_ID}/Configuration`;

    test.beforeAll(async () => {
        original = await admin('GET', configurationPath);
    });

    test.afterAll(async () => {
        if (original) await admin('POST', configurationPath, original);
    });

    test('the dashboard turns it on without a restart', async ({ page }) => {
        await signInAsAdmin(page);
        await page.evaluate(() => { location.hash = '#/configurationpage?name=SSO%20Authentication'; });
        await expect(page.locator('#sso-config-page')).toBeVisible();
        await page.locator('#AutoSignInProvider').selectOption(`OID/${PROVIDER}`);
        await page.locator('#EnableAutoSignIn').check({ force: true });
        await page.locator('#SaveAutoSignIn').click();
        await expect.poll(async () => (await admin('GET', configurationPath)).EnableAutoSignIn).toBe(true);

        const html = await page.evaluate(async () => (await fetch(location.pathname)).text());
        expect(html).toContain('data-sso-auto-sign-in');
    });

    test('a fresh browser signs in and an existing provider session signs in without a click', async ({ page }) => {
        await signInAtAuthelia(page, 'alice', `${JELLYFIN_URL}/web/`);
        await page.waitForURL(/#\/home/);
        const alice = await jellyfinUser('alice');
        await expect.poll(() => currentUserId(page)).toBe(alice.Id);
        expect(await quickConnectSignIn(await currentToken(page))).toEqual({ status: 200, userId: alice.Id });

        // Keep Authelia's session but remove Jellyfin's: the next visit needs no login form.
        await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
        await page.goto('about:blank');
        const exchange = page.waitForResponse(response => response.url().includes(`/sso/OID/Auth/${PROVIDER}`) && response.status() === 200);
        await page.goto(`${JELLYFIN_URL}/web/index.html#/home?tab=1`, { waitUntil: 'commit' });
        await exchange;
        await page.waitForURL(/#\/home\?tab=1$/);
        await expect.poll(() => currentUserId(page)).toBe(alice.Id);
        await expect(page.getByRole('button', { name: 'User Menu' })).toBeVisible();

        // A valid Jellyfin session survives a reload without a new trip to the provider.
        const token = await currentToken(page);
        const starts = [];
        page.on('request', request => { if (request.url().includes('/sso/OID/start/')) starts.push(request.url()); });
        await page.reload();
        await expect.poll(() => currentToken(page)).toBe(token);
        expect(starts).toEqual([]);
    });

    test('explicit login and Android WebView keep the normal login page', async ({ page, browser }) => {
        await page.goto(`${JELLYFIN_URL}/web/#/login`);
        await expect(page.locator('#txtManualName')).toBeVisible();
        await page.goto(`${JELLYFIN_URL}/web/`);
        await expect(page.locator('#txtManualName')).toBeVisible();

        const context = await browser.newContext({
            ignoreHTTPSErrors: true,
            userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/131.0.0.0 Mobile Safari/537.36'
        });
        try {
            const app = await context.newPage();
            await app.goto(`${JELLYFIN_URL}/web/`);
            await expect(app.locator('#txtManualName')).toBeVisible();
            expect(new URL(app.url()).origin).toBe(JELLYFIN_URL);
        } finally {
            await context.close();
        }
    });

    test('a revoked Jellyfin session is renewed through the existing provider session', async ({ page }) => {
        await signInAtAuthelia(page, 'alice', `${JELLYFIN_URL}/web/`);
        await page.waitForURL(/#\/home/);
        await expect.poll(() => currentToken(page)).toBeTruthy();
        const token = await currentToken(page);
        const revoked = await fetch(`${stack().base}/Sessions/Logout`, {
            method: 'POST',
            headers: { Authorization: `MediaBrowser Token="${token}"` }
        });
        expect(revoked.status).toBe(204);
        await page.evaluate(() => {
            const key = 'sso-auto-sign-in-tried';
            const attempt = JSON.parse(sessionStorage.getItem(key));
            attempt.at = Date.now() - 120000;
            sessionStorage.setItem(key, JSON.stringify(attempt));
        });
        const exchange = page.waitForResponse(response => response.url().includes(`/sso/OID/Auth/${PROVIDER}`) && response.status() === 200);
        await page.reload();
        await exchange;
        await page.waitForURL(/#\/home/);
        await expect.poll(() => currentUserId(page)).toBe((await jellyfinUser('alice')).Id);
        await expect.poll(() => currentToken(page)).toBeTruthy();
        await expect.poll(() => currentToken(page)).not.toBe(token);
    });

    test('signing out stays signed out across reloads and tabs', async ({ page }) => {
        await signInAtAuthelia(page, 'alice', `${JELLYFIN_URL}/web/`);
        await page.waitForURL(/#\/home/);
        await page.getByRole('button', { name: 'User Menu' }).click();
        await page.getByRole('menuitem', { name: 'Sign Out', exact: true }).click();
        await expect(page.locator('#txtManualName')).toBeVisible();
        await page.reload();
        await expect(page.locator('#txtManualName')).toBeVisible();
        const other = await page.context().newPage();
        await other.goto(`${JELLYFIN_URL}/web/`);
        await expect(other.locator('#txtManualName')).toBeVisible();
    });

    test('a rejected sign-in returns to login without trying the provider again', async ({ page }) => {
        await signInAtAuthelia(page, 'mallory', `${JELLYFIN_URL}/web/`);
        await expect(page.locator('#txtManualName')).toBeVisible();
        await page.goto(`${JELLYFIN_URL}/web/`);
        await expect(page.locator('#txtManualName')).toBeVisible();
        expect(await jellyfinUser('mallory')).toBeUndefined();
        expect(new URL(page.url()).origin).not.toBe(AUTH_URL);
    });

    test('a failed session exchange returns to login', async ({ page }) => {
        await page.route('**/sso/OID/Auth/*', route => route.fulfill({ status: 500, contentType: 'application/json', body: '{}' }));
        await signInAtAuthelia(page, 'alice', `${JELLYFIN_URL}/web/`);
        await expect(page.locator('#txtManualName')).toBeVisible();
        await page.reload();
        await expect(page.locator('#txtManualName')).toBeVisible();
    });

    test('disabling it restores the original page immediately', async ({ page }) => {
        await admin('POST', configurationPath, { ...original, EnableAutoSignIn: false });
        const response = await page.goto(`${JELLYFIN_URL}/web/`);
        expect(await response.text()).not.toContain('data-sso-auto-sign-in');
        await expect(page.locator('#txtManualName')).toBeVisible();
    });
});

// Last, because each makes Jellyfin's container again, as an admin changing the variable in a
// Compose file does: same data, new environment.
test.describe('JELLYFIN_ENABLE_PASSWORD_LOGIN', () => {
    test('false refuses every password, right or wrong, and signs no one out', async ({ page }) => {
        await restartJellyfin(page, { JELLYFIN_ENABLE_PASSWORD_LOGIN: 'false' });

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

    test('unset again, passwords sign in as before', async ({ page }) => {
        await restartJellyfin(page);

        expect(await passwordSignIn(ADMIN.name, ADMIN.password)).toBe(200);
        expect(await passwordSignIn(LOCAL_USER.name, LOCAL_USER.password)).toBe(200);
        expect(await passwordSignIn(LOCAL_USER.name, 'not-erins-password')).toBe(401);
        expect(await jellyfinLogs()).not.toContain('Password sign-in is off');
    });
});
