import { build } from 'esbuild';
import { cpSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { fileURLToPath } from 'url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const NODE_MODULES = join(__dirname, 'node_modules');
const BUILD_MODULES = join(__dirname, 'build', 'node_modules');

await build({
  entryPoints: ['src/index.ts'],
  bundle: true,
  platform: 'node',
  target: 'node18',
  format: 'esm',
  outfile: 'build/index.js',
  banner: {
    js: [
      // ESM shims for __dirname and require() (needed by bundled code)
      'import { createRequire as __createRequire } from "module";',
      'import { fileURLToPath as __fileURLToPath } from "url";',
      'import { dirname as __dirname_fn } from "path";',
      'const require = __createRequire(import.meta.url);',
      'const __filename = __fileURLToPath(import.meta.url);',
      'const __dirname = __dirname_fn(__filename);',
    ].join('\n'),
  },
  // Exclude node built-ins from bundle
  external: [
    'fs', 'path', 'os', 'url', 'module', 'crypto', 'events', 'stream', 'util',
    'net', 'tls', 'http', 'https', 'zlib', 'buffer', 'string_decoder',
    'child_process', 'worker_threads', 'node:*',
    // better-sqlite3 is a native module and cannot be bundled by esbuild.
    // It is deployed alongside the bundle in build/node_modules/.
    'better-sqlite3',
  ],
  sourcemap: false,
  minify: false, // Keep readable for debugging
});

// Copy better-sqlite3 and its runtime dependencies next to the bundle so that
// require('better-sqlite3') resolves correctly when build/index.js is executed.
const packages = ['better-sqlite3', 'bindings', 'file-uri-to-path'];
mkdirSync(BUILD_MODULES, { recursive: true });
for (const pkg of packages) {
  cpSync(join(NODE_MODULES, pkg), join(BUILD_MODULES, pkg), { recursive: true });
}

console.log('Build complete: build/index.js + build/node_modules/{better-sqlite3,bindings,file-uri-to-path}');
