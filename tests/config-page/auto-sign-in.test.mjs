import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

// The automatic sign-in script in a real browser, on a stand-in for Jellyfin Web's page, served
// where Jellyfin serves it, with the tag the plugin adds. The stand-in keeps a session where Jellyfin
// Web keeps one, and raises viewshow on a login page with the parameters Jellyfin Web gives it,
// which is all the script watches. Every request is answered here and recorded: going to the
// provider is a request for the address sign-in starts at. tests/e2e runs the script on a real
// Jellyfin Web with a real provider.

const script = await readFile(new URL('../../Jellyfin.Plugin.SsoAuthentication/Views/auto-sign-in.js', import.meta.url), 'utf8');

const ORIGIN = 'http://jellyfin.test';
const START = `${ORIGIN}/sso/OID/start/authelia`;
const TAG = '<script defer src="../SSOViews/auto-sign-in.js?v=1.0.0.0" data-sso-auto-sign-in="../sso/OID/start/authelia"></script>';
const ANDROID_WEBVIEW = 'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP2A.240805.005; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/131.0.6778.39 Mobile Safari/537.36';
const ANDROID_CHROME = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.6778.39 Mobile Safari/537.36';

// Jellyfin Web's page: the tag goes ahead of its scripts. Its main bundle here notes the address
// it started at.
const jellyfinWeb = (tag = TAG) => `<!doctype html><html><head><title>Jellyfin</title>${tag}`
    + '<script defer src="main.jellyfin.bundle.js"></script></head><body><div id="reactRoot"></div></body></html>';

// The script's, so tests can tell what a note records without the script.
function fingerprint(token) {
    let hash = 2166136261;
    for (let i = 0; i < token.length; i++) {
        hash = Math.imul(hash ^ token.charCodeAt(i), 16777619) >>> 0;
    }

    return token ? hash.toString(16) : '';
}

let browser;
before(async () => { browser = await chromium.launch({ headless: true }); });
after(async () => { await browser?.close(); });

async function browse({ userAgent, page = jellyfinWeb(), init, basePath = '', failure } = {}) {
    const context = await browser.newContext({ userAgent });
    if (init) {
        await context.addInitScript(init);
    }

    const errors = [];
    context.on('weberror', error => errors.push(error.error().message));
    await context.route('**/*', route => {
        const url = new URL(route.request().url());
        if (basePath && url.pathname.startsWith(basePath + '/')) {
            url.pathname = url.pathname.slice(basePath.length);
        }
        if (url.pathname === '/SSOViews/auto-sign-in.js') {
            return route.fulfill({ contentType: 'application/javascript', body: script });
        }

        if (url.pathname === '/web/main.jellyfin.bundle.js') {
            return route.fulfill({ contentType: 'application/javascript', body: 'window.startedAt = location.href;' });
        }

        if (url.pathname === '/web/' || url.pathname === '/web/index.html') {
            return route.fulfill({ contentType: 'text/html', body: page });
        }

        if (url.pathname === '/framed') {
            return route.fulfill({ contentType: 'text/html', body: '<!doctype html><iframe src="/web/index.html"></iframe>' });
        }

        if (failure && url.pathname.startsWith('/sso/')) {
            return route.fulfill({ status: 401, contentType: 'text/html', body: failure });
        }

        return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Elsewhere</title>' });
    });

    return { context, errors, tab: await context.newPage() };
}

// Resolves whether the browser goes to the provider, for an action started after, once it is
// there. The script decides as the page loads or the view shows, so half a second is ample.
const goes = (tab, timeout = 500) => tab.waitForRequest(START, { timeout }).then(
    () => tab.waitForURL(START).then(() => true),
    () => false);

// Loads Jellyfin Web's page afresh, as opening it does: never as a jump within the page, which an
// address differing only after the # would be. The tab keeps its sessionStorage.
async function open(tab, address) {
    await tab.goto('about:blank');
    const going = goes(tab);
    await tab.goto(`${ORIGIN}${address}`, { waitUntil: 'commit' });
    return going;
}

// Jellyfin Web's session, as it keeps it; "" when it has none.
async function keepSession(tab, token) {
    if (new URL(tab.url()).origin !== ORIGIN) {
        await tab.goto(`${ORIGIN}/blank`);
    }

    await tab.evaluate(token => localStorage.setItem('jellyfin_credentials', JSON.stringify({
        Servers: [{ Id: 'server', ManualAddress: 'http://jellyfin.test', UserId: token ? 'user' : null, AccessToken: token || null }]
    })), token);
}

// Jellyfin Web showing its login page, with the parameters it puts in the address.
async function showLogin(tab, params, id = 'loginPage') {
    const going = goes(tab);
    await tab.evaluate(({ params, id }) => {
        const view = document.createElement('div');
        view.id = id;
        document.body.appendChild(view);
        view.dispatchEvent(new CustomEvent('viewshow', { bubbles: true, detail: { type: null, properties: [], params } }));
    }, { params, id });
    return going;
}

const notes = tab => tab.evaluate(() => ({
    signedOut: JSON.parse(localStorage.getItem('sso-auto-sign-in-signed-out')),
    tried: JSON.parse(sessionStorage.getItem('sso-auto-sign-in-tried'))
}));

const bounce = { serverid: 'server', url: '/home' };

test('a browser with no session goes straight to the provider', async () => {
    const { context, tab, errors } = await browse();
    try {
        assert.equal(await open(tab, '/web/'), true);

        // The tab noted it went, with no copy of a session: it had none.
        await tab.goto(`${ORIGIN}/blank`);
        const { tried } = await notes(tab);
        assert.equal(tried.token, '');
        assert.equal(tried.page, '');
        assert.equal(typeof tried.at, 'number');
        assert.deepEqual(errors, []);
    } finally {
        await context.close();
    }
});

test('back with a session, it opens the page it was first opened at, before Jellyfin Web starts', async () => {
    const { context, tab, errors } = await browse();
    try {
        assert.equal(await open(tab, '/web/index.html#/details?id=1&serverId=server'), true);

        // The plugin's sign-in page stores the session and sends the browser to Jellyfin Web's start.
        await keepSession(tab, 'new-session');
        assert.equal(await open(tab, '/web/index.html'), false);
        await tab.waitForFunction(() => window.startedAt);

        assert.equal(await tab.evaluate(() => window.startedAt), `${ORIGIN}/web/index.html#/details?id=1&serverId=server`);
        const { tried } = await notes(tab);
        assert.equal(tried.token, fingerprint('new-session'));
        assert.equal(tried.back, true);
        assert.equal(tried.page, undefined);

        // Once only: the next load leaves the address alone.
        await open(tab, '/web/index.html');
        await tab.waitForFunction(() => window.startedAt);
        assert.equal(await tab.evaluate(() => window.startedAt), `${ORIGIN}/web/index.html`);
        assert.deepEqual(errors, []);
    } finally {
        await context.close();
    }
});

test('only a page of Jellyfin Web\'s own is opened again', async () => {
    for (const page of ['//elsewhere.test/', '/login?serverid=server', '/wizardstart', 'javascript:alert(1)', 7]) {
        const { context, tab } = await browse();
        try {
            await tab.goto(`${ORIGIN}/blank`);
            await tab.evaluate(page => sessionStorage.setItem('sso-auto-sign-in-tried', JSON.stringify({ token: '', at: Date.now(), page })), page);
            await keepSession(tab, 'new-session');

            await open(tab, '/web/index.html');
            await tab.waitForFunction(() => window.startedAt);

            assert.equal(await tab.evaluate(() => window.startedAt), `${ORIGIN}/web/index.html`, String(page));
        } finally {
            await context.close();
        }
    }
});

test('the pages Jellyfin Web shows people who are not signed in are left alone', async () => {
    const { context, tab } = await browse();
    try {
        for (const page of ['#/login', '#!/login', '#/login?serverid=server', '#/selectserver', '#/addserver', '#/forgotpassword', '#/forgotpasswordpin', '#/wizardstart', '#/wizarduser']) {
            assert.equal(await open(tab, `/web/${page}`), false, page);
        }
    } finally {
        await context.close();
    }
});

test('with a session, it waits for Jellyfin Web to say the session is not one it can use', async () => {
    const { context, tab, errors } = await browse();
    try {
        await keepSession(tab, 'revoked-session');
        assert.equal(await open(tab, '/web/'), false);

        // Jellyfin Web clears a session the server refuses, then sends the browser to log in.
        await keepSession(tab, '');
        assert.equal(await showLogin(tab, bounce), true);
        assert.deepEqual(errors, []);
    } finally {
        await context.close();
    }
});

test('only the login page, and only when Jellyfin Web names the server and the page, sends the browser', async () => {
    const { context, tab } = await browse();
    try {
        await keepSession(tab, 'session');
        await open(tab, '/web/');

        assert.equal(await showLogin(tab, bounce, 'indexPage'), false);
        assert.equal(await showLogin(tab, { serverid: 'server' }), false);
        assert.equal(await showLogin(tab, { url: '/home' }), false);
    } finally {
        await context.close();
    }
});

test('signing out leaves the browser on the login page until someone signs in', async () => {
    const { context, tab } = await browse();
    try {
        await keepSession(tab, 'session');
        await open(tab, '/web/');

        // Jellyfin Web's sign-out ends on its login page with no parameters.
        assert.equal(await showLogin(tab, {}), false);
        const { signedOut } = await notes(tab);
        assert.deepEqual(signedOut, { token: fingerprint('session') });
        assert.equal(JSON.stringify(signedOut).includes('"session"'), false);

        // Reloaded, in this tab or another, with the session gone or left behind, it stays.
        for (const session of ['', 'session']) {
            const other = await context.newPage();
            await keepSession(other, session);
            assert.equal(await open(other, '/web/'), false, `with "${session}"`);
            assert.equal(await showLogin(other, bounce), false, `with "${session}"`);
            await other.close();
        }

        // Signing in again, here with the sign-in button, turns it back on.
        await keepSession(tab, 'next-session');
        await open(tab, '/web/');
        assert.deepEqual(await notes(tab), { signedOut: null, tried: null });
        await keepSession(tab, '');
        assert.equal(await showLogin(tab, bounce), true);
    } finally {
        await context.close();
    }
});

test('opening the login page on purpose keeps the browser there', async () => {
    const { context, tab } = await browse();
    try {
        assert.equal(await open(tab, '/web/#/login'), false);
        await showLogin(tab, {});

        const other = await context.newPage();
        assert.equal(await open(other, '/web/'), false);
    } finally {
        await context.close();
    }
});

test('a trip to the provider that brings no session back leaves that tab on the login page', async () => {
    const { context, tab } = await browse();
    try {
        assert.equal(await open(tab, '/web/'), true);

        // Back from a provider that said no, or from its login page.
        assert.equal(await open(tab, '/web/'), false);
        assert.equal(await showLogin(tab, bounce), false);

        // A tab of its own tries once.
        const other = await context.newPage();
        assert.equal(await open(other, '/web/'), true);
    } finally {
        await context.close();
    }
});

test('a session the provider brings back that Jellyfin Web refuses ends on the login page', async () => {
    const { context, tab } = await browse();
    try {
        assert.equal(await open(tab, '/web/'), true);
        await keepSession(tab, 'refused-session');
        assert.equal(await open(tab, '/web/index.html'), false);

        await keepSession(tab, '');
        assert.equal(await showLogin(tab, bounce), false);
    } finally {
        await context.close();
    }
});

test('a session that lasted is renewed when it ends', async () => {
    const { context, tab } = await browse();
    try {
        await tab.goto(`${ORIGIN}/blank`);
        await tab.evaluate(token => sessionStorage.setItem('sso-auto-sign-in-tried', JSON.stringify({ token, at: Date.now() - 120000, back: true })), fingerprint('lasting-session'));
        await keepSession(tab, 'lasting-session');
        assert.equal(await open(tab, '/web/'), false);

        await keepSession(tab, '');
        assert.equal(await showLogin(tab, bounce), true);
    } finally {
        await context.close();
    }
});

test('Jellyfin Web in a frame stays where it is', async () => {
    const { context, tab } = await browse();
    try {
        const going = goes(tab);
        await tab.goto(`${ORIGIN}/framed`);
        await tab.frames()[1].waitForFunction(() => window.startedAt);

        assert.equal(await going, false);
        assert.deepEqual(await notes(tab), { signedOut: null, tried: null });
    } finally {
        await context.close();
    }
});

test('an app that gives Jellyfin Web a NativeShell stays on its login page', async () => {
    const { context, tab } = await browse({ init: () => { window.NativeShell = {}; } });
    try {
        assert.equal(await open(tab, '/web/'), false);
        assert.equal(await showLogin(tab, bounce), false);
    } finally {
        await context.close();
    }
});

test('an Android WebView stays on its login page, and Chrome on Android does not', async () => {
    const webview = await browse({ userAgent: ANDROID_WEBVIEW });
    try {
        assert.equal(await open(webview.tab, '/web/'), false);
        assert.equal(await showLogin(webview.tab, bounce), false);
    } finally {
        await webview.context.close();
    }

    const chrome = await browse({ userAgent: ANDROID_CHROME });
    try {
        assert.equal(await open(chrome.tab, '/web/'), true);
    } finally {
        await chrome.context.close();
    }
});

test('a tab that cannot note that it went does not go', async () => {
    const { context, tab } = await browse({
        init: () => Object.defineProperty(window, 'sessionStorage', { get() { throw new DOMException('Blocked', 'SecurityError'); } })
    });
    try {
        assert.equal(await open(tab, '/web/'), false);
        await keepSession(tab, '');
        assert.equal(await showLogin(tab, bounce), false);
    } finally {
        await context.close();
    }
});

test('without the address, the script does nothing', async () => {
    const { context, tab } = await browse({ page: jellyfinWeb('<script defer src="../SSOViews/auto-sign-in.js"></script>') });
    try {
        assert.equal(await open(tab, '/web/'), false);
        assert.equal(await showLogin(tab, bounce), false);
    } finally {
        await context.close();
    }
});

test('a start attribute cannot execute script or choose an arbitrary destination', async () => {
    for (const value of [
        'javascript:window.ssoInjected=true',
        'JaVaScRiPt:window.ssoInjected=true',
        '&#106;avascript:window.ssoInjected=true',
        'data:text/html,unsafe',
        '//elsewhere.test/path',
        `${ORIGIN}.elsewhere.test/sso/OID/start/authelia`,
        '../sso/LDAP/start/authelia',
        '../sso/OID/start/%ZZ',
        '../sso/OID/start/%FF',
        '../sso/OID/start/%2e%2e',
        '../sso/OID/start/.'
    ]) {
        const tag = `<script defer src="../SSOViews/auto-sign-in.js" data-sso-auto-sign-in="${value}"></script>`;
        const { context, tab, errors } = await browse({ page: jellyfinWeb(tag) });
        try {
            assert.equal(await open(tab, '/web/'), false, value);
            assert.equal(tab.url(), `${ORIGIN}/web/`, value);
            assert.equal(await tab.evaluate(() => window.ssoInjected), undefined, value);
            assert.deepEqual(await notes(tab), { signedOut: null, tried: null }, value);
            assert.deepEqual(errors, [], value);
        } finally {
            await context.close();
        }
    }
});

test('both protocols keep escaped provider names in one segment under a base path', async () => {
    for (const protocol of ['OID', 'SAML']) {
        const provider = 'Home%20Media%2F%C3%BC%3F';
        const tag = `<script defer src="../SSOViews/auto-sign-in.js" data-sso-auto-sign-in="../sso/${protocol}/start/${provider}"></script>`;
        const { context, tab, errors } = await browse({ basePath: '/media', page: jellyfinWeb(tag) });
        try {
            await tab.goto(`${ORIGIN}/media/web/`, { waitUntil: 'commit' });
            await tab.waitForURL(`${ORIGIN}/media/sso/${protocol}/start/${provider}`);
            assert.deepEqual(errors, []);
        } finally {
            await context.close();
        }
    }
});

test('session setup checks the origin instead of a hostname prefix', async () => {
    const { context, tab } = await browse();
    try {
        for (const address of [`${ORIGIN}.elsewhere.test/blank`, `${ORIGIN}:8080/blank`]) {
            await tab.goto(address);
            await keepSession(tab, 'test-session');
            assert.equal(tab.url(), `${ORIGIN}/blank`);
            await tab.goto(address);
            assert.equal(await tab.evaluate(() => localStorage.getItem('jellyfin_credentials')), null);
        }
    } finally {
        await context.close();
    }
});

test('unreadable notes and sessions count as none', async () => {
    const { context, tab, errors } = await browse();
    try {
        await tab.goto(`${ORIGIN}/blank`);
        await tab.evaluate(() => {
            localStorage.setItem('jellyfin_credentials', '{not json');
            localStorage.setItem('sso-auto-sign-in-signed-out', '"a string"');
            sessionStorage.setItem('sso-auto-sign-in-tried', 'null');
        });

        assert.equal(await open(tab, '/web/'), true);
        assert.deepEqual(errors, []);
    } finally {
        await context.close();
    }
});

test('the provider and failure fallback keep a reverse proxy path prefix', async () => {
    const failure = '<!doctype html><head><title>Sign-in failed</title>'
        + '<script defer src="../../../SSOViews/auto-sign-in.js?v=1" data-sso-auto-fallback="../../../web/index.html#/login"></script>'
        + '</head><body>Permission denied</body>';
    const { context, tab, errors } = await browse({ basePath: '/media', failure });
    try {
        const starts = [];
        tab.on('request', request => { if (request.url().includes('/sso/OID/start/')) starts.push(request.url()); });
        await tab.goto(`${ORIGIN}/media/web/`, { waitUntil: 'commit' });
        await tab.waitForURL(`${ORIGIN}/media/web/index.html#/login`);
        await tab.waitForFunction(() => window.startedAt);

        assert.deepEqual(starts, [`${ORIGIN}/media/sso/OID/start/authelia`]);
        assert.equal((await notes(tab)).tried.back, undefined);
        assert.deepEqual(errors, []);
    } finally {
        await context.close();
    }
});

test('a manual sign-in error stays readable, including after an earlier completed attempt', async () => {
    const failure = '<!doctype html><head><title>Sign-in failed</title>'
        + '<script defer src="../../../SSOViews/auto-sign-in.js?v=1" data-sso-auto-fallback="../../../web/index.html#/login"></script>'
        + '</head><body>Permission denied</body>';
    const { context, tab, errors } = await browse({ failure });
    try {
        for (const attempt of [null, { back: true, token: fingerprint('old-session') }]) {
            await tab.goto(`${ORIGIN}/blank`);
            await tab.evaluate(value => sessionStorage.setItem('sso-auto-sign-in-tried', JSON.stringify(value)), attempt);
            const response = await tab.goto(`${ORIGIN}/sso/OID/redirect/authelia`);
            assert.equal(response.status(), 401);
            assert.equal(await tab.title(), 'Sign-in failed');
            assert.equal(tab.url(), `${ORIGIN}/sso/OID/redirect/authelia`);
        }
        assert.deepEqual(errors, []);
    } finally {
        await context.close();
    }
});

test('a fallback attribute is only a marker for the fixed local login page', async () => {
    for (const value of ['javascript:window.ssoInjected=true', '//elsewhere.test/path']) {
        const failure = '<!doctype html><head><title>Sign-in failed</title>'
            + `<script defer src="../../../SSOViews/auto-sign-in.js" data-sso-auto-fallback="${value}"></script>`
            + '</head><body>Permission denied</body>';
        const { context, tab, errors } = await browse({ basePath: '/media', failure });
        try {
            await tab.goto(`${ORIGIN}/media/web/`, { waitUntil: 'commit' });
            await tab.waitForURL(`${ORIGIN}/media/web/index.html#/login`);
            await tab.waitForFunction(() => window.startedAt);
            assert.equal(await tab.evaluate(() => window.ssoInjected), undefined);
            assert.deepEqual(errors, []);
        } finally {
            await context.close();
        }
    }
});
