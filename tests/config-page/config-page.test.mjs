import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

// The settings page in a real browser against a stubbed ApiClient: what it shows, and above
// all what it saves. Saving writes the whole plugin configuration back, so anything the page
// does not show has to survive a save untouched. Account links most of all: losing them would
// sign every linked user in to a new, empty account.

const PLUGIN_ID = '3c3b1a1d-7470-466f-a565-b3041e54724d';
const html = await readFile(new URL('../../Jellyfin.Plugin.SsoAuthentication/Config/configPage.html', import.meta.url), 'utf8');
const script = await readFile(new URL('../../Jellyfin.Plugin.SsoAuthentication/Config/config.js', import.meta.url), 'utf8');

const configuration = () => ({
    SamlConfigs: { keycloak: { SamlEndpoint: 'https://keycloak.example.test/saml', Enabled: true } },
    OidConfigs: {
        authelia: {
            OidEndpoint: 'https://auth.example.test',
            OidClientId: 'jellyfin',
            OidSecret: 'saved-secret',
            Enabled: true,
            EnableAuthorization: true,
            EnableAllFolders: false,
            EnabledFolders: ['movies'],
            Roles: ['jellyfin-users'],
            AdminRoles: ['jellyfin-admins'],
            RoleClaim: 'groups',
            OidScopes: ['groups'],
            CanonicalLinks: { alice: '8c1f4e2a9d3b4a6eb7c51e0f2d3c4b5a' },
            FutureSetting: 'preserve-me'
        },
        other: { OidEndpoint: 'https://other.example.test', Enabled: false, CanonicalLinks: { bob: '5a4b3c2d1e0f4a9b8c7d6e5f4a3b2c1d' } }
    }
});

let browser;
before(async () => { browser = await chromium.launch({ headless: true }); });
after(async () => { await browser?.close(); });

async function load() {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    // Nothing leaves the browser: every request the page makes is answered empty.
    await page.route('**/*', route => route.fulfill({ body: '' }));
    await page.goto('http://jellyfin.test/web/');
    await page.setContent(html);
    await page.evaluate(({ config, pluginId }) => {
        window.saved = [];
        window.alerts = [];
        window.configuration = config;
        const ownPlugin = id => { if (id !== pluginId) throw new Error('Wrong plugin ID ' + id); };
        window.ApiClient = {
            getUrl: path => 'http://jellyfin.test/' + path.replace(/^\//, ''),
            getPluginConfiguration: async id => { ownPlugin(id); return structuredClone(window.configuration); },
            updatePluginConfiguration: async (id, value) => {
                ownPlugin(id);
                window.configuration = structuredClone(value);
                window.saved.push(structuredClone(value));
                return {};
            },
            getJSON: async () => ({ Items: [{ Id: 'movies', Name: 'Movies' }, { Id: 'shows', Name: 'Shows' }] })
        };
        window.Dashboard = {
            processPluginConfigurationUpdateResult: () => {},
            alert: text => window.alerts.push(text)
        };
    }, { config: configuration(), pluginId: PLUGIN_ID });

    // Jellyfin loads config.js as the page's controller: a module whose default export is
    // called with the page.
    await page.evaluate(async source => {
        const url = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
        try {
            (await import(url)).default(document.querySelector('#sso-config-page'));
        } finally {
            URL.revokeObjectURL(url);
        }
    }, script);
    await page.waitForFunction(() => document.querySelectorAll('#selectProvider option').length === 2);
    return { page, errors };
}

async function loadProvider(page, name) {
    await page.selectOption('#selectProvider', name);
    await page.click('#LoadProvider');
    await page.waitForFunction(value => document.querySelector('#OidProviderName').value === value, name);
}

test('lists the OpenID providers and loads one into the form', async () => {
    const { page, errors } = await load();
    try {
        assert.deepEqual(await page.$$eval('#selectProvider option', options => options.map(o => o.value)), ['authelia', 'other']);

        await loadProvider(page, 'authelia');

        assert.equal(await page.inputValue('#OidEndpoint'), 'https://auth.example.test');
        assert.equal(await page.inputValue('#OidClientId'), 'jellyfin');
        assert.equal(await page.isChecked('#Enabled'), true);
        assert.equal(await page.isChecked('#EnabledFolders input[data-id="movies"]'), true);
        assert.equal(await page.isChecked('#EnabledFolders input[data-id="shows"]'), false);
        assert.equal(await page.inputValue('#Roles'), 'jellyfin-users');
        assert.deepEqual(errors, []);
    } finally {
        await page.close();
    }
});

test('saving a provider keeps its account links and everything the page does not show', async () => {
    const { page, errors } = await load();
    try {
        await loadProvider(page, 'authelia');
        await page.fill('#OidClientId', 'updated-client');
        await page.fill('#Roles', 'jellyfin-users\nfamily');
        await page.check('#EnabledFolders input[data-id="shows"]', { force: true });

        await page.click('#SaveProvider');
        await page.waitForFunction(() => window.saved.length === 1);
        const saved = await page.evaluate(() => window.saved[0]);
        const authelia = saved.OidConfigs.authelia;

        assert.equal(authelia.OidClientId, 'updated-client');
        assert.deepEqual(authelia.Roles, ['jellyfin-users', 'family']);
        assert.deepEqual(authelia.EnabledFolders, ['movies', 'shows']);
        assert.equal(authelia.OidSecret, 'saved-secret');
        assert.deepEqual(authelia.CanonicalLinks, { alice: '8c1f4e2a9d3b4a6eb7c51e0f2d3c4b5a' });
        assert.equal(authelia.FutureSetting, 'preserve-me');
        assert.deepEqual(saved.OidConfigs.other, configuration().OidConfigs.other);
        assert.deepEqual(saved.SamlConfigs, configuration().SamlConfigs);
        assert.deepEqual(await page.evaluate(() => window.alerts), ['Settings saved.']);
        assert.deepEqual(errors, []);
    } finally {
        await page.close();
    }
});

test('a provider saved under a new name is added beside the others', async () => {
    const { page, errors } = await load();
    try {
        await loadProvider(page, 'other');
        await page.fill('#OidProviderName', 'pocket-id');
        await page.fill('#OidEndpoint', 'https://id.example.test');

        await page.click('#SaveProvider');
        await page.waitForFunction(() => window.saved.length === 1);
        const saved = await page.evaluate(() => window.saved[0]);

        assert.deepEqual(Object.keys(saved.OidConfigs).sort(), ['authelia', 'other', 'pocket-id']);
        assert.equal(saved.OidConfigs['pocket-id'].OidEndpoint, 'https://id.example.test');
        assert.equal(saved.OidConfigs.other.OidEndpoint, 'https://other.example.test');
        assert.deepEqual(errors, []);
    } finally {
        await page.close();
    }
});

test('deleting a provider removes only that provider, once confirmed', async () => {
    const { page, errors } = await load();
    try {
        await page.selectOption('#selectProvider', 'other');
        page.once('dialog', dialog => dialog.accept());
        await page.click('#DeleteProvider');
        await page.waitForFunction(() => window.saved.length === 1);
        const saved = await page.evaluate(() => window.saved[0]);

        assert.deepEqual(Object.keys(saved.OidConfigs), ['authelia']);
        assert.deepEqual(saved.OidConfigs.authelia.CanonicalLinks, { alice: '8c1f4e2a9d3b4a6eb7c51e0f2d3c4b5a' });
        assert.deepEqual(saved.SamlConfigs, configuration().SamlConfigs);
        assert.deepEqual(errors, []);
    } finally {
        await page.close();
    }
});

test('declining the confirmation deletes nothing', async () => {
    const { page } = await load();
    try {
        await page.selectOption('#selectProvider', 'other');
        page.once('dialog', dialog => dialog.dismiss());
        await page.click('#DeleteProvider');
        await page.waitForTimeout(300);

        assert.deepEqual(await page.evaluate(() => window.saved), []);
    } finally {
        await page.close();
    }
});
