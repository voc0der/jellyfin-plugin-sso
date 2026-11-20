import { expect } from '@playwright/test';
import { ADMIN, AUTH_URL, JELLYFIN_URL, PROVIDER, idpPassword, jellyfin } from './stack.mjs';

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
    await expect(page).toHaveURL(new RegExp('^' + AUTH_URL.replace(/\./g, '\\.')));
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

// The status Jellyfin answers a sign-in with this name and an empty password, as anyone on its
// login page could try: 200 means the account is open.
export async function emptyPasswordSignIn(name) {
    const response = await fetch(`${stack().base}/Users/AuthenticateByName`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            Authorization: 'MediaBrowser Client="SSO harness", Device="harness", DeviceId="sso-e2e-empty-password", Version="1.0.0"'
        },
        body: JSON.stringify({ Username: name, Pw: '' })
    });
    return response.status;
}

// Who Jellyfin Web thinks is signed in, or null until it knows.
export function currentUserId(page) {
    return page.evaluate(() => window.ApiClient?.getCurrentUserId?.() ?? null);
}
