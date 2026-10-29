const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { createStateTransport } = require('../../runner/ecs/shared-state.cjs');
const { execute } = require('../../runner/ecs/entrypoint.cjs');
const { runBundledRunner } = require('../../runner/ecs/bootstrap.cjs');
const { EventEmitter } = require('node:events');
const { spawnSync } = require('node:child_process');

const env = { BOLD_PLAYWRIGHT_STATE_GET_URL: 'https://storage.example/state?signature=secret', BOLD_PLAYWRIGHT_STATE_PUT_URL: 'https://storage.example/state?signature=secret' };
function directory(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bold-ecs-state-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  return root;
}
function envelope(files) { return JSON.stringify({ format: 1, files, checksum: crypto.createHash('sha256').update(JSON.stringify(files)).digest('hex') }); }

test('No S3 settings leave the existing directory workflow untouched', async () => {
  const state = createStateTransport({ env: {}, request: () => { throw new Error('must not call'); } });
  assert.equal(state.enabled, false); await state.restore(); await state.save();
});
test('Partial/insecure URLs fail without leaking them', () => {
  assert.throws(() => createStateTransport({ env: { BOLD_PLAYWRIGHT_STATE_GET_URL: env.BOLD_PLAYWRIGHT_STATE_GET_URL } }), error => !error.message.includes('signature'));
  assert.throws(() => createStateTransport({ env: { ...env, BOLD_PLAYWRIGHT_STATE_PUT_URL: 'http://storage.example/state' } }));
});
test('State can cross tasks without sharing a filesystem; product files are excluded', async t => {
  const first = directory(t); const second = directory(t);
  fs.mkdirSync(path.join(first, 'tests/API/ResponseLogs'), { recursive: true }); fs.mkdirSync(path.join(first, 'storage'));
  fs.writeFileSync(path.join(first, 'tests/API/ResponseLogs/resource.json'), '{"id":"resource"}');
  fs.writeFileSync(path.join(first, 'storage/login.json'), 'session'); fs.writeFileSync(path.join(first, 'config.json'), 'never-upload-this');
  let data;
  await createStateTransport({ root: first, env, request: async (_, options) => { data = options.body; return new Response('', { status: 200 }); } }).save();
  assert.ok(!data.includes('never-upload-this'));
  await createStateTransport({ root: second, env, request: async () => new Response(data) }).restore();
  assert.equal(fs.readFileSync(path.join(second, 'storage/login.json'), 'utf8'), 'session');
  assert.equal(fs.readFileSync(path.join(second, 'tests/API/ResponseLogs/resource.json'), 'utf8'), '{"id":"resource"}');
});
test('Missing state stops validation', async t => {
  await assert.rejects(createStateTransport({ root: directory(t), env, request: async () => new Response('', { status: 404 }) }).restore(), /unavailable/);
});
test('Integrity mismatch is rejected before writing files', async t => {
  const root = directory(t); const state = JSON.parse(envelope([{ path: 'storage/auth.json', content: 'YQ==' }])); state.checksum = 'bad';
  await assert.rejects(createStateTransport({ root, env, request: async () => new Response(JSON.stringify(state)) }).restore(), /integrity/);
  assert.equal(fs.existsSync(path.join(root, 'storage')), false);
});
for (const name of ['storage/../../config.json', '../config.json', 'ResponseLogs/../secret', 'storage/a\\..\\config.json', 'configuration/privatekeys.dat']) {
  test('Rejects unsafe state path: ' + name, async t => {
    await assert.rejects(createStateTransport({ root: directory(t), env, request: async () => new Response(envelope([{ path: name, content: 'YQ==' }])) }).restore());
  });
}
test('Network failures do not expose signed URLs', async t => {
  const state = createStateTransport({ root: directory(t), env, request: async () => { throw new Error(env.BOLD_PLAYWRIGHT_STATE_GET_URL); } });
  await assert.rejects(state.restore(), error => !error.message.includes('signature'));
  await assert.rejects(state.save(), error => !error.message.includes('signature'));
});
test('EFS wrapper delegates unchanged to the original version-specific runner', async () => {
  let called;
  assert.equal(await execute({ args: ['pre'], transport: { enabled: false }, run: async args => { called = args; return 0; } }), 0);
  assert.deepEqual(called, ['pre']);
});
test('S3 post restores before tests and saves after tests', async () => {
  const order = [];
  assert.equal(await execute({ args: ['post'], transport: { enabled: true, restore: async () => order.push('restore'), save: async () => order.push('save') }, run: async () => { order.push('test'); return 0; } }), 0);
  assert.deepEqual(order, ['restore', 'test', 'save']);
});
test('Failed tests still save resource IDs for cleanup', async () => {
  let saved = false;
  assert.equal(await execute({ args: ['pre'], transport: { enabled: true, save: async () => { saved = true; } }, run: async () => 1 }), 1);
  assert.ok(saved);
});
test('Restore failure neither runs tests nor overwrites the existing state', async () => {
  let executed = false; let saved = false;
  await assert.rejects(execute({ args: ['cleanup'], transport: { enabled: true, restore: async () => { throw new Error('missing'); }, save: async () => { saved = true; } }, run: async () => { executed = true; } }));
  assert.equal(executed, false); assert.equal(saved, false);
});
test('State save failure cannot return a passing exit code from the original runner', async () => {
  await assert.rejects(execute({ args: ['pre'], transport: { enabled: true, save: async () => { throw new Error('denied'); } }, run: async () => 0 }));
});

test('In-memory bootstrap uses the original runner and preserves S3 stage ordering', async () => {
  const order = [];
  const sources = { './entrypoint.cjs': fs.readFileSync(path.join(__dirname, '../../runner/ecs/entrypoint.cjs'), 'utf8') };
  const hostRequire = name => {
    if (name === './shared-state.cjs') return { createStateTransport: () => ({ enabled: true,
      restore: async () => order.push('restore'), save: async () => order.push('save') }) };
    if (name === 'node:child_process') return { spawn: (binary, args, options) => {
      assert.equal(binary, process.execPath);
      assert.deepEqual(args, ['/app/docker-entrypoint.js', 'post']);
      assert.equal(options.stdio, 'inherit');
      order.push('original-runner');
      const child = new EventEmitter();
      queueMicrotask(() => child.emit('exit', 0, null));
      return child;
    } };
    throw new Error('Unexpected module: ' + name);
  };
  await runBundledRunner(sources, ['post'], hostRequire);
  assert.deepEqual(order, ['restore', 'original-runner', 'save']);
});

test('Bundled modules are cached rather than executing their source twice', () => {
  const sources = {
    './entrypoint.cjs': "module.exports.main = args => [require('./state.cjs'), require('./state.cjs'), args];",
    './state.cjs': 'module.exports = {};'
  };
  const [first, second, args] = runBundledRunner(sources, ['cleanup']);
  assert.equal(first, second);
  assert.deepEqual(args, ['cleanup']);
});

test('ECS node -e command forwards pre, post, and cleanup arguments correctly', () => {
  const bootstrap = fs.readFileSync(path.join(__dirname, '../../runner/ecs/bootstrap.cjs'), 'utf8');
  const sources = { './entrypoint.cjs': 'module.exports.main = args => console.log(JSON.stringify(args));' };
  for (const stage of ['pre', 'post', 'cleanup']) {
    const command = bootstrap + '\nrunBundledRunner(' + JSON.stringify(sources) + ', process.argv.slice(1));';
    const result = spawnSync(process.execPath, ['-e', command, stage], { encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    assert.deepEqual(JSON.parse(result.stdout), [stage]);
  }
});
