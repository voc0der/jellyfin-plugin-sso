// Boots a throwaway Jellyfin with this repository's release archive of the plugin installed,
// Authelia as the OpenID provider, and nginx terminating TLS in front of both, then connects
// them the way an admin would.
//
// All three are real because the seams are what this harness exists to check: Jellyfin decides
// whether the plugin loads and how its sign-in page hands a session to Jellyfin Web, Authelia
// decides what a standards-following provider sends back, and the proxy is how Jellyfin is
// reached in practice, which decides the addresses the plugin builds.

import { execFile } from 'node:child_process';
import { generateKeyPairSync, pbkdf2Sync, randomBytes } from 'node:crypto';
import { mkdir, readdir, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const exec = promisify(execFile);
const sleep = ms => new Promise(done => setTimeout(done, ms));

const HERE = dirname(fileURLToPath(import.meta.url));
export const HARNESS_ROOT = resolve(HERE, '..');
export const REPO_ROOT = resolve(HARNESS_ROOT, '../..');

// The newest Jellyfin release: sign-in has to work on what people run.
export const JELLYFIN_IMAGE = process.env.JELLYFIN_IMAGE ?? 'jellyfin/jellyfin:12.4';
export const AUTHELIA_IMAGE = process.env.AUTHELIA_IMAGE ?? 'authelia/authelia:4.39';
export const PROXY_IMAGE = process.env.PROXY_IMAGE ?? 'nginx:1.27-alpine';

export const PLUGIN_ID = '3c3b1a1d-7470-466f-a565-b3041e54724d';
export const DOMAIN = 'sso.test';
export const AUTH_URL = `https://auth.${DOMAIN}`;
export const JELLYFIN_URL = `https://jellyfin.${DOMAIN}`;
export const PROVIDER = 'authelia';
export const CLIENT_ID = 'jellyfin';
export const CLIENT_SECRET = 'sso-e2e-harness-client-secret-not-a-real-one';
export const ADMIN = { name: 'admin', password: 'harness-admin-pw' };

// Authelia's accounts. Their groups decide what the plugin lets each one do: the provider
// below lets jellyfin-users in and makes jellyfin-admins administrators.
export const IDP_USERS = {
    alice: { displayname: 'Alice', groups: ['jellyfin-users'] },
    carol: { displayname: 'Carol', groups: ['jellyfin-users', 'jellyfin-admins'] },
    dave: { displayname: 'Dave', groups: ['jellyfin-users'] },
    frank: { displayname: 'Frank', groups: ['jellyfin-users'] },
    mallory: { displayname: 'Mallory', groups: ['visitors'] }
};
export const idpPassword = name => `${name}-harness-pw`;

// The picture every Authelia account carries in its picture claim. Jellyfin Web ships it, so
// the plugin can fetch it from inside Jellyfin's container with no server of its own.
export const PICTURE_PATH = '/web/assets/img/avatar.png';
const PICTURE_URL = `http://localhost:8096${PICTURE_PATH}`;

// A Jellyfin account made with a password, for linking an Authelia identity to.
export const LOCAL_USER = { name: 'erin', password: 'erin-harness-pw' };

// A Jellyfin account with Jellyfin's password provider and no password, which Jellyfin opens
// to an empty password: how 9p4's plugin left the accounts it made on Jellyfin 12.
export const EXPOSED_USER = { name: 'frank' };

const NETWORK = 'sso-e2e';
const JELLYFIN = 'sso-e2e-jellyfin';
const AUTHELIA = 'sso-e2e-authelia';
const PROXY = 'sso-e2e-proxy';
export const CONTAINERS = [JELLYFIN, AUTHELIA, PROXY];
const CLIENT_AUTH = 'MediaBrowser Client="SSO harness", Device="harness", DeviceId="sso-e2e", Version="1.0.0"';

async function docker(...args) {
    const { stdout } = await exec('docker', args, { maxBuffer: 64 * 1024 * 1024 });
    return stdout.trim();
}

// Rootless Docker can briefly report a new container's filesystem busy, notably right after a
// container of the same name was removed, so copies into one are retried.
async function dockerCopy(source, target) {
    for (let attempt = 1; ; attempt++) {
        try {
            return await docker('cp', source, target);
        } catch (error) {
            if (attempt >= 5 || !/busy|failed to mount/i.test(String(error.stderr ?? error.message))) {
                throw error;
            }

            await sleep(1000 * attempt);
        }
    }
}

// The release archive, unpacked as an admin installing by hand would: build.sh refuses to
// package anything Jellyfin already provides, so this is also the check that the archive
// holds everything sign-in needs. PLUGIN_ZIP installs another archive instead, such as a
// published release or an earlier one to compare with. Staged inside the harness folder:
// rootless Docker cannot see this process's /tmp, so a staging folder there would arrive empty.
export async function stagePlugin(build) {
    const plugins = join(build, 'plugins');
    await rm(plugins, { recursive: true, force: true });
    await mkdir(plugins, { recursive: true });

    let archive = process.env.PLUGIN_ZIP ? resolve(process.env.PLUGIN_ZIP) : null;
    if (!archive) {
        await exec(join(REPO_ROOT, 'build.sh'), [], { cwd: REPO_ROOT, maxBuffer: 64 * 1024 * 1024 });
        const built = (await readdir(join(REPO_ROOT, 'artifacts'))).find(name => /^SSO_Authentication_.*\.zip$/.test(name));
        if (!built) {
            throw new Error('build.sh produced no SSO_Authentication_*.zip in artifacts/');
        }

        archive = join(REPO_ROOT, 'artifacts', built);
    }

    // Archives from this repository hold one folder; older ones hold the files themselves.
    const { stdout } = await exec('unzip', ['-Z1', archive]);
    const flat = stdout.split('\n').some(entry => /^[^/]+\.dll$/.test(entry));
    await exec('unzip', ['-q', '-o', archive, '-d', flat ? join(plugins, 'SSO Authentication') : plugins]);
    return plugins;
}

// A throwaway CA and a certificate for *.sso.test. The proxy serves it, Jellyfin trusts the CA
// for its calls to Authelia, and the browser is told to accept it.
async function makeCertificates(dir) {
    await mkdir(dir, { recursive: true });
    const ca = join(dir, 'ca');
    const leaf = join(dir, DOMAIN);
    const openssl = (...args) => exec('openssl', args);

    // Strict verifiers want these extensions on both certificates.
    await openssl('req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '2',
        '-keyout', `${ca}.key`, '-out', `${ca}.crt`, '-subj', '/CN=SSO e2e harness CA',
        '-addext', 'basicConstraints=critical,CA:TRUE',
        '-addext', 'keyUsage=critical,keyCertSign,cRLSign',
        '-addext', 'subjectKeyIdentifier=hash');
    await writeFile(`${leaf}.ext`, [
        `subjectAltName=DNS:*.${DOMAIN},DNS:${DOMAIN}`,
        'extendedKeyUsage=serverAuth',
        'basicConstraints=critical,CA:FALSE',
        'keyUsage=critical,digitalSignature,keyEncipherment',
        'subjectKeyIdentifier=hash',
        'authorityKeyIdentifier=keyid:always'
    ].join('\n') + '\n');
    await openssl('req', '-newkey', 'rsa:2048', '-nodes', '-keyout', `${leaf}.key`, '-out', `${leaf}.csr`, '-subj', `/CN=*.${DOMAIN}`);
    await openssl('x509', '-req', '-in', `${leaf}.csr`, '-CA', `${ca}.crt`, '-CAkey', `${ca}.key`,
        '-CAcreateserial', '-days', '2', '-out', `${leaf}.crt`, '-extfile', `${leaf}.ext`);

    return { caCrt: `${ca}.crt`, crt: `${leaf}.crt`, key: `${leaf}.key` };
}

// Authelia's PBKDF2 digest format, for its users' passwords and the client's secret. The
// Base64 is passlib's adapted alphabet: unpadded, with '.' in place of '+'.
function autheliaHash(password) {
    const salt = randomBytes(16);
    const iterations = 310000;
    const key = pbkdf2Sync(password, salt, iterations, 64, 'sha512');
    const b64 = buffer => buffer.toString('base64').replace(/=+$/, '').replace(/\+/g, '.');
    return `$pbkdf2-sha512$${iterations}$${b64(salt)}$${b64(key)}`;
}

const secret = () => randomBytes(32).toString('hex');

function autheliaUsers() {
    const lines = ['users:'];
    for (const [name, user] of Object.entries(IDP_USERS)) {
        lines.push(`  ${name}:`,
            '    disabled: false',
            `    displayname: '${user.displayname}'`,
            `    password: '${autheliaHash(idpPassword(name))}'`,
            `    email: '${name}@${DOMAIN}'`,
            `    picture: '${PICTURE_URL}'`,
            '    groups:',
            ...user.groups.map(group => `      - '${group}'`));
    }

    return lines.join('\n') + '\n';
}

// The Jellyfin client follows the plugin's own provider documentation for Authelia, on
// Authelia's defaults otherwise: preferred_username and groups reach the plugin from the
// userinfo endpoint, and the plugin authenticates its token request with client_secret_post.
// It runs with pushed authorization off: the plugin sends that request with Basic
// authentication but the token request in the body, and Authelia allows one method per client.
function autheliaConfiguration() {
    const { privateKey } = generateKeyPairSync('rsa', {
        modulusLength: 2048,
        privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
        publicKeyEncoding: { type: 'spki', format: 'pem' }
    });
    const key = privateKey.trim().split('\n').map(line => '          ' + line).join('\n');

    return `# Generated by tests/e2e/harness/stack.mjs
server:
  address: 'tcp://:9091/'
log:
  level: 'info'
identity_validation:
  reset_password:
    jwt_secret: '${secret()}'
authentication_backend:
  password_reset:
    disable: true
  file:
    path: '/config/users.yml'
access_control:
  default_policy: 'one_factor'
session:
  secret: '${secret()}'
  cookies:
    - domain: '${DOMAIN}'
      authelia_url: '${AUTH_URL}'
storage:
  encryption_key: '${secret()}'
  local:
    path: '/config/db.sqlite3'
notifier:
  filesystem:
    filename: '/config/notification.txt'
identity_providers:
  oidc:
    hmac_secret: '${secret()}'
    jwks:
      - key_id: 'e2e'
        algorithm: 'RS256'
        use: 'sig'
        key: |
${key}
    clients:
      - client_id: '${CLIENT_ID}'
        client_name: 'Jellyfin'
        client_secret: '${autheliaHash(CLIENT_SECRET)}'
        public: false
        authorization_policy: 'one_factor'
        consent_mode: 'implicit'
        require_pkce: true
        pkce_challenge_method: 'S256'
        redirect_uris:
          - '${JELLYFIN_URL}/sso/OID/redirect/${PROVIDER}'
        scopes: ['openid', 'profile', 'groups']
        response_types: ['code']
        grant_types: ['authorization_code']
        access_token_signed_response_alg: 'none'
        userinfo_signed_response_alg: 'none'
        token_endpoint_auth_method: 'client_secret_post'
`;
}

// TLS for both names, the way a reverse proxy in front of Jellyfin is usually set up. Upstreams
// resolve through Docker's DNS at request time, so nginx starts before they do.
function proxyConfiguration() {
    const server = (name, upstream) => `
    server {
        listen 443 ssl;
        server_name ${name}.${DOMAIN};
        ssl_certificate /etc/nginx/certs/${DOMAIN}.crt;
        ssl_certificate_key /etc/nginx/certs/${DOMAIN}.key;
        location / {
            set $upstream ${upstream};
            proxy_pass $upstream;
            proxy_http_version 1.1;
            proxy_set_header Host $host;
            proxy_set_header X-Forwarded-Proto https;
            proxy_set_header X-Forwarded-Host $host;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection $connection_upgrade;
        }
    }`;

    return `# Generated by tests/e2e/harness/stack.mjs
events { worker_connections 1024; }
http {
    resolver 127.0.0.11 valid=10s ipv6=off;
    map $http_upgrade $connection_upgrade { default upgrade; '' close; }
    proxy_buffer_size 128k;
    proxy_buffers 4 256k;
    proxy_busy_buffers_size 256k;
${server('auth', 'http://authelia:9091')}
${server('jellyfin', 'http://jellyfin:8096')}
}
`;
}

async function waitFor(check, timeoutMs, what, container) {
    const deadline = Date.now() + timeoutMs;
    let last = 'never responded';
    while (Date.now() < deadline) {
        // A container that exited will not come up; say so now rather than at the deadline.
        if (container && (await docker('inspect', '-f', '{{.State.Running}}', container).catch(() => 'false')) !== 'true') {
            throw new Error(`${what} stopped`);
        }

        try {
            const result = await check();
            if (result === true) {
                return;
            }

            last = String(result);
        } catch (error) {
            last = error.message;
        }

        await sleep(1000);
    }

    throw new Error(`${what} did not come up within ${timeoutMs}ms (last: ${last})`);
}

async function jsonUp(url, accept) {
    const response = await fetch(url);
    const type = response.headers.get('content-type') ?? '';
    if (!response.ok || !type.includes('json')) {
        return `${response.status} ${type}`;
    }

    return accept(await response.json()) || 'not ready yet';
}

// Jellyfin rewrites its configuration between wizard steps and briefly answers 404 or 503.
async function retry(fn, attempts = 30) {
    let last;
    for (let i = 0; i < attempts; i++) {
        try {
            return await fn();
        } catch (error) {
            last = error;
            await sleep(1000);
        }
    }

    throw last;
}

export async function jellyfin(base, method, path, { token, body } = {}) {
    const response = await fetch(base + path, {
        method,
        headers: {
            'Content-Type': 'application/json',
            Authorization: token ? `${CLIENT_AUTH}, Token="${token}"` : CLIENT_AUTH
        },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await response.text();
    if (!response.ok) {
        throw new Error(`${method} ${path} -> ${response.status} ${text.slice(0, 300)}`);
    }

    return text ? JSON.parse(text) : null;
}

async function logsOf(name, lines) {
    const args = lines ? ['logs', '--tail', String(lines), name] : ['logs', name];
    return exec('docker', args, { maxBuffer: 64 * 1024 * 1024 })
        .then(({ stdout, stderr }) => stdout + stderr)
        .catch(() => '');
}

export async function stopStack({ logDir } = {}) {
    if (logDir) {
        await mkdir(logDir, { recursive: true });
        for (const name of CONTAINERS) {
            const logs = await logsOf(name);
            if (logs) {
                await writeFile(join(logDir, `${name}.log`), logs);
            }
        }
    }

    await docker('rm', '-f', '-v', ...CONTAINERS).catch(() => {});
    await docker('network', 'rm', NETWORK).catch(() => {});
}

// The provider as an admin would add it, following the Authelia section of providers.md.
export function providerConfiguration(overrides = {}) {
    return {
        OidEndpoint: AUTH_URL,
        OidClientId: CLIENT_ID,
        OidSecret: CLIENT_SECRET,
        Enabled: true,
        EnableAuthorization: true,
        EnableAllFolders: true,
        EnabledFolders: [],
        AdminRoles: ['jellyfin-admins'],
        Roles: ['jellyfin-users'],
        EnableFolderRoles: false,
        FolderRoleMapping: [],
        RoleClaim: 'groups',
        OidScopes: ['groups'],
        DefaultUsernameClaim: 'preferred_username',
        AvatarUrlFormat: '@{picture}',
        DisablePushedAuthorization: true,
        SchemeOverride: 'https',
        // Saved by every sign-in started at /sso/OID/start/, and what 4.0.x settings files
        // carry. The linking page starts at the older /p/ path, which keeps whatever is saved.
        NewPath: true,
        ...overrides
    };
}

export async function startStack() {
    const build = join(HARNESS_ROOT, '.build');
    await mkdir(build, { recursive: true });
    const plugins = await stagePlugin(build);
    const certs = await makeCertificates(join(build, 'certs'));

    const ca = await readFile(certs.caCrt);
    await stopStack();
    await docker('network', 'create', NETWORK);

    // Authelia's and nginx's files go in through the environment and are written by the
    // container itself: some rootless Docker setups cannot copy into a container that has
    // never run, except into a declared volume. The variables avoid Authelia's AUTHELIA_
    // prefix, which it would read as settings.
    await docker('run', '--detach', '--name', AUTHELIA, '--network', NETWORK, '--network-alias', 'authelia',
        '-e', 'TZ=UTC', '-e', `SSO_E2E_CONFIGURATION=${autheliaConfiguration()}`, '-e', `SSO_E2E_USERS=${autheliaUsers()}`,
        '--entrypoint', 'sh', AUTHELIA_IMAGE, '-c',
        'mkdir -p /config && printf "%s" "$SSO_E2E_CONFIGURATION" > /config/configuration.yml '
        + '&& printf "%s" "$SSO_E2E_USERS" > /config/users.yml '
        + '&& unset SSO_E2E_CONFIGURATION SSO_E2E_USERS && exec authelia --config /config/configuration.yml');

    await docker('run', '--detach', '--name', PROXY, '--network', NETWORK,
        '--network-alias', `auth.${DOMAIN}`, '--network-alias', `jellyfin.${DOMAIN}`,
        '--publish', '127.0.0.1::443',
        '-e', `SSO_E2E_NGINX=${proxyConfiguration()}`,
        '-e', `SSO_E2E_CERT=${await readFile(certs.crt, 'utf8')}`,
        '-e', `SSO_E2E_KEY=${await readFile(certs.key, 'utf8')}`,
        '--entrypoint', 'sh', PROXY_IMAGE, '-c',
        `mkdir -p /etc/nginx/certs && printf "%s" "$SSO_E2E_NGINX" > /etc/nginx/nginx.conf `
        + `&& printf "%s" "$SSO_E2E_CERT" > /etc/nginx/certs/${DOMAIN}.crt `
        + `&& printf "%s" "$SSO_E2E_KEY" > /etc/nginx/certs/${DOMAIN}.key `
        + '&& unset SSO_E2E_NGINX SSO_E2E_CERT SSO_E2E_KEY && exec nginx -g "daemon off;"');

    // Jellyfin's /config is a volume, which can be copied into before the first start. The
    // harness CA goes there too, and SSL_CERT_FILE makes .NET trust it, and only it, for the
    // plugin's calls to Authelia.
    const trust = join(plugins, '..', 'trust');
    await rm(trust, { recursive: true, force: true });
    await mkdir(trust, { recursive: true });
    await writeFile(join(trust, 'sso-e2e-ca.crt'), ca);
    await docker('create', '--name', JELLYFIN, '--network', NETWORK, '--network-alias', 'jellyfin',
        '--publish', '127.0.0.1::8096', '-e', 'TZ=UTC', '-e', 'SSL_CERT_FILE=/config/sso-e2e-ca.crt', JELLYFIN_IMAGE);
    await dockerCopy(plugins, `${JELLYFIN}:/config/`);
    await dockerCopy(join(trust, 'sso-e2e-ca.crt'), `${JELLYFIN}:/config/sso-e2e-ca.crt`);
    await docker('start', JELLYFIN);

    const port = async (name, inner) => (await docker('port', name, inner)).split('\n')[0].split(':').pop();
    const base = `http://127.0.0.1:${await port(JELLYFIN, '8096/tcp')}`;
    const proxyPort = Number(await port(PROXY, '443/tcp'));

    // Asked inside its own container: the browser tests go through the proxy anyway.
    await waitFor(async () => {
        const health = await docker('exec', AUTHELIA, 'wget', '-q', '-O', '-', 'http://127.0.0.1:9091/api/health').catch(error => String(error.stderr ?? error.message));
        return /"status":\s*"OK"/.test(health) || health.slice(0, 120);
    }, 60_000, 'Authelia', AUTHELIA).catch(async error => {
        throw new Error(`${error.message}\n${await logsOf(AUTHELIA, 40)}`);
    });

    // A booting Jellyfin first answers with a placeholder, then 503, then the real thing.
    await waitFor(() => jsonUp(`${base}/System/Info/Public`, body => 'Version' in body), 180_000, 'Jellyfin', JELLYFIN).catch(async error => {
        throw new Error(`${error.message}\n${await logsOf(JELLYFIN, 60)}`);
    });
    let info = await jellyfin(base, 'GET', '/System/Info/Public');
    if (!info.StartupWizardCompleted) {
        await waitFor(() => jsonUp(`${base}/Startup/Configuration`, () => true), 120_000, 'the Jellyfin wizard');
        await retry(() => jellyfin(base, 'POST', '/Startup/Configuration', {
            body: { UICulture: 'en-US', MetadataCountryCode: 'US', PreferredMetadataLanguage: 'en' }
        }));
        await retry(() => jellyfin(base, 'GET', '/Startup/User'));
        await retry(() => jellyfin(base, 'POST', '/Startup/User', { body: { Name: ADMIN.name, Password: ADMIN.password } }));
        await retry(() => jellyfin(base, 'POST', '/Startup/RemoteAccess', {
            body: { EnableRemoteAccess: true, EnableAutomaticPortMapping: false }
        }));
        await retry(() => jellyfin(base, 'POST', '/Startup/Complete'));
    }

    info = await retry(() => jellyfin(base, 'GET', '/System/Info/Public'));
    const adminSession = await retry(() => jellyfin(base, 'POST', '/Users/AuthenticateByName', {
        body: { Username: ADMIN.name, Pw: ADMIN.password }
    }));
    const adminToken = adminSession.AccessToken;

    // The archive's own meta.json says which plugin it is: this one, unless PLUGIN_ZIP named
    // another to compare with.
    const { stdout: metaPath } = await exec('find', [plugins, '-name', 'meta.json']);
    const installedId = JSON.parse(await readFile(metaPath.split('\n')[0], 'utf8')).guid;
    const loaded = await jellyfin(base, 'GET', '/Plugins', { token: adminToken });
    const plugin = loaded.find(entry => entry.Id.replace(/-/g, '') === installedId.replace(/-/g, ''));
    if (!plugin || plugin.Status !== 'Active') {
        throw new Error(`The plugin did not load on Jellyfin ${info.Version} (status: ${plugin?.Status ?? 'not listed'}).\n${await logsOf(JELLYFIN, 80)}`);
    }

    // Trust the proxy's X-Forwarded-* headers, as an admin behind a reverse proxy does.
    const proxyAddress = await docker('inspect', '-f', '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}', PROXY);
    const network = await jellyfin(base, 'GET', '/System/Configuration/network', { token: adminToken });
    network.KnownProxies = [proxyAddress];
    await jellyfin(base, 'POST', '/System/Configuration/network', { token: adminToken, body: network });

    await jellyfin(base, 'POST', `/sso/OID/Add/${PROVIDER}`, { token: adminToken, body: providerConfiguration() });

    const local = await jellyfin(base, 'POST', '/Users/New', {
        token: adminToken,
        body: { Name: LOCAL_USER.name, Password: LOCAL_USER.password }
    });
    const exposed = await jellyfin(base, 'POST', '/Users/New', { token: adminToken, body: { Name: EXPOSED_USER.name } });

    return {
        base,
        proxyPort,
        jellyfinVersion: info.Version,
        pluginVersion: plugin.Version,
        adminToken,
        localUserId: local.Id,
        exposedUserId: exposed.Id
    };
}
