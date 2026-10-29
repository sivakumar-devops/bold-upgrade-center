const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require(process.env.BOLD_UC_PLAYWRIGHT_MODULE || 'playwright');

async function main() {
    const artifacts = path.resolve(process.argv[2]);
    const root = path.resolve(__dirname, '../../wwwroot');
    const browser = await chromium.launch({ headless: true });
    const errors = [];
    const checked = [];
    try {
        const page = await browser.newPage();
        page.on('pageerror', error => errors.push(error.message));
        await page.route('http://upgrade-center.test/**', async route => {
            const url = new URL(route.request().url());
            const snapshots = { '/login': 'login.html', '/login-error': 'login-error.html', '/profile': 'profile.html' };
            if (snapshots[url.pathname]) {
                return route.fulfill({ contentType: 'text/html', body: fs.readFileSync(path.join(artifacts, snapshots[url.pathname])) });
            }
            const relative = url.pathname.replace(/^\/upgrade-center\//, '');
            const file = path.resolve(root, relative);
            if (!file.startsWith(root + path.sep) || !fs.existsSync(file)) {
                errors.push('Missing asset: ' + url.pathname);
                return route.fulfill({ status: 404 });
            }
            const contentType = { '.css': 'text/css', '.js': 'application/javascript', '.svg': 'image/svg+xml' }[path.extname(file)] || 'application/octet-stream';
            return route.fulfill({ contentType, body: fs.readFileSync(file) });
        });
        for (const [label, viewport] of [
            ['desktop', { width: 1440, height: 900 }],
            ['mobile', { width: 390, height: 844 }],
            ['small-mobile', { width: 320, height: 640 }],
            ['landscape', { width: 800, height: 400 }]
        ]) {
            await page.setViewportSize(viewport);
            await page.goto('http://upgrade-center.test/login');
            await page.evaluate(() => document.fonts.ready);
            const panel = await page.locator('.standalone-login-panel').boundingBox();
            assert.ok(panel.width <= 492 && panel.x >= 0 && panel.x + panel.width <= viewport.width);
            assert.ok(await page.locator('.standalone-login-brand img').evaluate(image => image.complete && image.naturalWidth > 0));
            assert.equal(await page.locator('#Password').getAttribute('type'), 'password');
            await page.locator('#Password').fill('ui-test-password');
            await page.getByRole('button', { name: 'Show password' }).click();
            assert.equal(await page.locator('#Password').getAttribute('type'), 'text');
            await page.getByRole('button', { name: 'Hide password' }).click();
            assert.equal(await page.locator('#Password').getAttribute('type'), 'password');
            await page.locator('#Password').fill('');
            assert.equal(await page.locator('form').getAttribute('action'), '/upgrade-center/login');
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
            await page.screenshot({ path: path.join(artifacts, 'login-' + label + '.png'), fullPage: true });
            checked.push('login-' + label);
        }
        await page.setViewportSize({ width: 390, height: 844 });
        await page.goto('http://upgrade-center.test/login-error');
        assert.ok(await page.getByRole('alert').isVisible());
        assert.equal(await page.locator('#Password').inputValue(), '');
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        await page.screenshot({ path: path.join(artifacts, 'login-error-mobile.png'), fullPage: true });

        for (const width of [1440, 320]) {
            await page.setViewportSize({ width, height: 800 });
            await page.goto('http://upgrade-center.test/profile');
            await page.getByLabel('Open user menu').focus();
            await page.keyboard.press('Enter');
            const signout = page.getByRole('button', { name: 'Sign out' });
            assert.ok(await signout.isVisible());
            assert.equal(await signout.evaluate(button => getComputedStyle(button).borderTopWidth), '0px');
            const bounds = await signout.boundingBox();
            assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width);
            assert.equal(await page.locator('.user-menu-signout').getAttribute('method'), 'post');
            assert.ok(await page.locator('.user-menu-signout input[name="__RequestVerificationToken"]').count());
            assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
            await page.screenshot({ path: path.join(artifacts, 'profile-' + width + '.png'), fullPage: true });
            checked.push('profile-' + width);
        }
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ passed: checked, errorPage: true, browserErrors: errors, artifacts }, null, 2));
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
