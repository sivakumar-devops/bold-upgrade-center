const { spawn } = require('node:child_process');
const { createStateTransport } = require('./shared-state.cjs');

async function execute({ args = process.argv.slice(2), env = process.env, run, transport = createStateTransport({ env }) } = {}) {
  const mode = args[0] || 'pre';
  if (!['pre', 'post', 'cleanup'].includes(mode)) throw new Error('ECS runner requires pre, post, or cleanup mode.');
  // Restore must finish before the original runner can perform any product operation.
  if (transport.enabled && mode !== 'pre') await transport.restore();
  let exitCode;
  try { exitCode = await run(args); }
  finally {
    // A failed test may still have created resources. Retain its IDs for cleanup.
    if (transport.enabled) await transport.save();
  }
  return exitCode;
}

function runOriginal(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['/app/docker-entrypoint.js', ...args], { stdio: 'inherit', env: process.env });
    const terminate = () => child.kill('SIGTERM');
    process.once('SIGTERM', terminate);
    child.once('error', error => { process.removeListener('SIGTERM', terminate); reject(error); });
    child.once('exit', (code, signal) => { process.removeListener('SIGTERM', terminate); resolve(signal ? 143 : code ?? 1); });
  });
}

function main(args = process.argv.slice(2)) {
  return execute({ args, run: runOriginal }).then(code => { process.exitCode = code; }).catch(() => {
    // Signed URLs and test credentials must not appear in bootstrap errors.
    console.error('ECS runner bootstrap/state transfer failed. Validation was not accepted; inspect storage permissions and retained state.');
    process.exitCode = 1;
  });
}
if (require.main === module) main();
module.exports = { execute, main };
