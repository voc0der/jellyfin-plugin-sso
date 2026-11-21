import { expect } from '@playwright/test';
import { ADMIN, AUTH_URL, JELLYFIN_URL, PROVIDER, idpPassword, jellyfin, recreateJellyfin } from './stack.mjs';

export function stack() {
    return JSON.parse(process.env.SSO_E2E);
}

export function admin(method, path, body) {
    const { base, adminToken } = stack();
    return jellyfin(base, method, path, { token: adminToken, body });
}

export async function jellyfinUser(name) {
    const users = await admin('GET', '/Users');
    return users.find(user => user.Name === name);
}

// Starts a sign-in the way a "Sign in with SSO" button does, and signs in at Authelia's portal.
export async function signInAtAuthelia(page, name, start = `${JELLYFIN_URL}/sso/OID/start/${PROVIDER}`) {
    await page.goto(start);
    await page.waitForURL(url => url.origin === AUTH_URL);
    await page.locator('#username-textfield').fill(name);
    await page.locator('#password-textfield').fill(idpPassword(name));
    await page.locator('#sign-in-button').click();
}

// The whole sign-in: Authelia, back to the plugin's page, and on into Jellyfin Web, which
// counts as signed in once it has restored the session the plugin's page handed it.
export async function signInWithSso(page, name) {
    await signInAtAuthelia(page, name);
    await page.waitForURL(/\/web\/(index\.html)?#\/home/, { timeout: 45_000 });
    await expect.poll(() => currentUserId(page), { timeout: 30_000 }).not.toBeNull();
}

// Jellyfin Web's own login form, for accounts that have a password.
export async function signInWithPassword(page, user) {
    await page.goto(`${JELLYFIN_URL}/web/`);
    await page.locator('#txtManualName').fill(user.name);
    await page.locator('#txtManualPassword').fill(user.password);
    await page.locator('#txtManualPassword').press('Enter');
    await page.waitForURL(/#\/home/, { timeout: 30_000 });
}

export const signInAsAdmin = page => signInWithPassword(page, ADMIN);

// The status Jellyfin answers a sign-in with this name and password, as anyone on its login page
// could try: 200 means it signed in.
export async function passwordSignIn(name, password) {
    const response = await fetch(`${stack().base}/Users/AuthenticateByName`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            Authorization: 'MediaBrowser Client="SSO harness", Device="harness", DeviceId="sso-e2e-password", Version="1.0.0"'
        },
        body: JSON.stringify({ Username: name, Pw: password })
    });
    return response.status;
}

// The same with an empty password: 200 means the account is open to anyone.
export const emptyPasswordSignIn = name => passwordSignIn(name, '');

// An app signing in with Quick Connect, approved from a session already signed in, as Jellyfin
// Web approves a code: the account the app was signed in to, or the status that refused it.
export async function quickConnectSignIn(approverToken) {
    const app = 'MediaBrowser Client="SSO harness TV", Device="harness TV", DeviceId="sso-e2e-quick-connect", Version="1.0.0"';
    const { base } = stack();
    const call = (path, { token, body } = {}) => fetch(base + path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: token ? `${app}, Token="${token}"` : app },
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    const started = await call('/QuickConnect/Initiate');
    if (!started.ok) {
        return { status: started.status };
    }

    const { Secret, Code } = await started.json();
    const approved = await call(`/QuickConnect/Authorize?code=${encodeURIComponent(Code)}`, { token: approverToken });
    if (!approved.ok) {
        return { status: approved.status };
    }

    const signedIn = await call('/Users/AuthenticateWithQuickConnect', { body: { Secret } });
    return signedIn.ok ? { status: signedIn.status, userId: (await signedIn.json()).User.Id } : { status: signedIn.status };
}

// Makes Jellyfin's container again with this environment, and waits until the browser reaches it
// through the proxy, as the next sign-in will.
export async function restartJellyfin(page, environment = {}) {
    await recreateJellyfin(environment);
    await expect.poll(async () => {
        const response = await page.goto(`${JELLYFIN_URL}/System/Info/Public`).catch(() => null);
        return response?.status();
    }, { timeout: 30_000 }).toBe(200);
}

// Who Jellyfin Web thinks is signed in, or null until it knows.
export function currentUserId(page) {
    return page.evaluate(() => window.ApiClient?.getCurrentUserId?.() ?? null);
}

// The token of the session Jellyfin Web is signed in with.
export function currentToken(page) {
    return page.evaluate(() => window.ApiClient?.accessToken?.() ?? null);
}
