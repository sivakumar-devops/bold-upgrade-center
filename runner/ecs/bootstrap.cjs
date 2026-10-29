function runBundledRunner(sources, args = process.argv.slice(1), hostRequire = require) {
  const cache = new Map();
  function load(name) {
    if (!Object.hasOwn(sources, name)) return hostRequire(name);
    if (!cache.has(name)) {
      const loaded = { exports: {} };
      cache.set(name, loaded);
      new Function('require', 'module', sources[name])(load, loaded);
    }
    return cache.get(name).exports;
  }
  return load('./entrypoint.cjs').main(args);
}

module.exports = { runBundledRunner };
