const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const maximumBytes = 64 * 1024 * 1024;
const maximumFiles = 5000;

function createStateTransport({ root = '/app', env = process.env, request = fetch } = {}) {
  const getUrl = env.BOLD_PLAYWRIGHT_STATE_GET_URL;
  const putUrl = env.BOLD_PLAYWRIGHT_STATE_PUT_URL;
  const enabled = Boolean(getUrl || putUrl);
  if (enabled && (!getUrl || !putUrl || ![getUrl, putUrl].every(value => {
    try { return new URL(value).protocol === 'https:'; } catch { return false; }
  }))) throw new Error('ECS shared state requires both protected HTTPS transfer URLs.');
  const directories = { ResponseLogs: path.join(root, 'tests', 'API', 'ResponseLogs'), storage: path.join(root, 'storage') };
  const digest = files => crypto.createHash('sha256').update(JSON.stringify(files)).digest('hex');

  function localFile(name) {
    const parts = name.split('/');
    const directory = directories[parts.shift()];
    if (!directory || parts.length === 0 || parts.some(part => !part || part === '.' || part === '..' || part.includes('\\') || part.includes(':') || part.includes('\0')))
      throw new Error('Invalid ECS shared-state file path.');
    const target = path.join(directory, ...parts);
    let current = target;
    while (current !== path.dirname(directory)) {
      if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Shared-state symbolic links are not supported.');
      current = path.dirname(current);
    }
    return target;
  }

  async function restore() {
    if (!enabled) return;
    let response;
    try { response = await request(getUrl, { signal: AbortSignal.timeout(60000) }); }
    catch { throw new Error('ECS validation shared state could not be downloaded.'); }
    if (!response.ok) throw new Error('Required ECS validation shared state is unavailable.');
    let bytes = 0;
    const chunks = [];
    for await (const chunk of response.body) {
      bytes += chunk.length;
      if (bytes > maximumBytes) throw new Error('ECS shared state exceeds the supported size.');
      chunks.push(chunk);
    }
    const state = JSON.parse(Buffer.concat(chunks).toString('utf8'));
    if (state.format !== 1 || !Array.isArray(state.files) || state.files.length > maximumFiles || digest(state.files) !== state.checksum)
      throw new Error('ECS shared-state integrity validation failed.');
    const files = state.files.map(item => {
      if (typeof item.path !== 'string' || typeof item.content !== 'string' || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(item.content))
        throw new Error('Invalid ECS shared-state content.');
      return { target: localFile(item.path), content: Buffer.from(item.content, 'base64') };
    });
    if (new Set(files.map(item => item.target)).size !== files.length) throw new Error('Duplicate ECS shared-state file path.');
    for (const item of files) {
      fs.mkdirSync(path.dirname(item.target), { recursive: true });
      fs.writeFileSync(item.target, item.content, { mode: 0o600 });
    }
    console.log('ECS validation shared state restored.');
  }

  async function save() {
    if (!enabled) return;
    const files = [];
    let total = 0;
    function visit(directory, prefix) {
      if (!fs.existsSync(directory)) return;
      if (fs.lstatSync(directory).isSymbolicLink()) throw new Error('Shared-state symbolic links are not supported.');
      for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
        if (entry.isSymbolicLink()) throw new Error('Shared-state symbolic links are not supported.');
        const name = prefix + '/' + entry.name;
        const target = localFile(name);
        if (entry.isDirectory()) visit(target, name);
        else if (entry.isFile()) {
          total += fs.statSync(target).size;
          if (total > maximumBytes / 2 || files.length >= maximumFiles) throw new Error('ECS shared state exceeds the supported size.');
          files.push({ path: name, content: fs.readFileSync(target).toString('base64') });
        }
      }
    }
    for (const [name, directory] of Object.entries(directories)) visit(directory, name);
    const payload = JSON.stringify({ format: 1, checksum: digest(files), files });
    if (Buffer.byteLength(payload) > maximumBytes) throw new Error('ECS shared state exceeds the supported size.');
    let response;
    try { response = await request(putUrl, { method: 'PUT', body: payload, signal: AbortSignal.timeout(60000) }); }
    catch { throw new Error('ECS validation shared state could not be saved.'); }
    if (!response.ok) throw new Error('ECS validation shared-state upload failed.');
    console.log('ECS validation shared state committed.');
  }
  return { enabled, restore, save };
}

module.exports = { createStateTransport };
