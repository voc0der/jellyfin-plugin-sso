import { join } from 'node:path';
import { AUTHELIA_IMAGE, CONTAINERS, HARNESS_ROOT, JELLYFIN_IMAGE, JELLYFIN_URL, startStack, stopStack } from './stack.mjs';

// Builds the release archive, boots Jellyfin, Authelia and the proxy with the provider set up,
// and hands the addresses to the tests through the environment.
export default async function globalSetup() {
    const started = Date.now();
    const stack = await startStack();
    process.env.SSO_E2E = JSON.stringify(stack);

    const seconds = ((Date.now() - started) / 1000).toFixed(1);
    console.log(`Jellyfin ${stack.jellyfinVersion} (${JELLYFIN_IMAGE}) with SSO Authentication ${stack.pluginVersion}`);
    console.log(`Authelia (${AUTHELIA_IMAGE}); ${JELLYFIN_URL} and the provider through 127.0.0.1:${stack.proxyPort}`);
    console.log(`Ready in ${seconds}s`);

    return async () => {
        if (process.env.HARNESS_KEEP) {
            console.log(`HARNESS_KEEP set: left running. Jellyfin admin API at ${stack.base} (admin / harness-admin-pw).`);
            console.log(`Browse with: --host-resolver-rules="MAP *.sso.test 127.0.0.1:${stack.proxyPort}" --ignore-certificate-errors`);
            console.log(`Remove with: docker rm -f -v ${CONTAINERS.join(' ')} && docker network rm sso-e2e`);
            return;
        }

        await stopStack({ logDir: join(HARNESS_ROOT, 'test-results') });
    };
}
