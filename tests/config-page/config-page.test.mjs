import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { after, before, test } from "node:test";
import { chromium } from "playwright";

let browser;
before(async () => {
    browser = await chromium.launch({ headless: true });
});
after(async () => {
    await browser?.close();
});

test("provider settings load, save and delete through the real page controls", async () => {
    const page = await browser.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(error.message));
    try {
        // No network, server, real provider or credentials are involved in this harness.
        await page.route("**/*", (route) => route.fulfill({ body: "" }));
        await page.goto("http://jellyfin.test/");
        await page.setContent(
            await readFile("SSO-Auth/Config/configPage.html", "utf8"),
        );
        await page.evaluate(() => {
            window.saved = [];
            window.alerts = [];
            window.configuration = {
                SamlConfigs: { retained: { Enabled: false } },
                OidConfigs: {
                    example: {
                        OidEndpoint: "https://id.example.test",
                        OidClientId: "jellyfin",
                        OidSecret: "test-placeholder",
                        Enabled: true,
                        EnableAuthorization: true,
                        EnabledFolders: ["movies"],
                        Roles: ["viewers"],
                    },
                },
            };
            window.ApiClient = {
                getUrl: (path) =>
                    `http://jellyfin.test/${path.replace(/^\//, "")}`,
                getPluginConfiguration: async (id) => {
                    if (id !== "505ce9d1-d916-42fa-86ca-673ef241d7df")
                        throw new Error("Wrong plugin ID");
                    return structuredClone(window.configuration);
                },
                updatePluginConfiguration: async (id, config) => {
                    if (id !== "505ce9d1-d916-42fa-86ca-673ef241d7df")
                        throw new Error("Wrong plugin ID");
                    window.configuration = structuredClone(config);
                    window.saved.push(structuredClone(config));
                    return {};
                },
                getJSON: async () => ({
                    Items: [{ Id: "movies", Name: "Movies" }],
                }),
            };
            window.Dashboard = {
                processPluginConfigurationUpdateResult: () => {},
                alert: (message) => window.alerts.push(message),
            };
        });
        const source = await readFile("SSO-Auth/Config/config.js", "utf8");
        await page.evaluate(async (script) => {
            const url = URL.createObjectURL(
                new Blob([script], { type: "text/javascript" }),
            );
            try {
                const module = await import(url);
                module.default(document.body);
            } finally {
                URL.revokeObjectURL(url);
            }
        }, source);
        await page.waitForFunction(
            () =>
                document.querySelector("#selectProvider option")?.value ===
                "example",
        );
        await page.click("#LoadProvider");
        await page.waitForFunction(
            () => document.querySelector("#OidClientId").value === "jellyfin",
        );
        assert.equal(await page.isChecked("#Enabled"), true);
        assert.equal(
            await page.isChecked('#EnabledFolders input[data-id="movies"]'),
            true,
        );
        await page.fill("#OidClientId", "updated-client");
        await page.fill("#Roles", "viewers\nfamily");
        await page.click("#SaveProvider");
        await page.waitForFunction(() => window.saved.length === 1);
        const saved = await page.evaluate(() => window.saved[0]);
        assert.equal(saved.OidConfigs.example.OidClientId, "updated-client");
        assert.deepEqual(saved.OidConfigs.example.Roles, ["viewers", "family"]);
        assert.deepEqual(saved.OidConfigs.example.EnabledFolders, ["movies"]);
        assert.deepEqual(saved.SamlConfigs, { retained: { Enabled: false } });
        page.once("dialog", (dialog) => dialog.accept());
        await page.click("#DeleteProvider");
        await page.waitForFunction(() => window.saved.length === 2);
        assert.deepEqual(
            await page.evaluate(() => window.configuration.OidConfigs),
            {},
        );
        assert.deepEqual(errors, []);
    } finally {
        await page.close();
    }
});
