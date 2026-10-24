import { execSync } from 'node:child_process';
import path from 'path';
import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// A build identity baked into the bundle and used as the persisted-query-cache
// buster (see main.tsx). Each release ships a distinct commit, so this value
// changes on every deploy; on restore, TanStack discards a persisted cache
// stamped with a different buster instead of rehydrating it as fresh. Without
// it, data cached before a server upgrade (e.g. the gear-menu version banner and
// update-available flag) outlives the upgrade and shows stale until the browser's
// localStorage is cleared. The commit SHA is deterministic per commit, so an
// unchanged source tree produces an unchanged bundle (unlike a build timestamp).
function resolveBuildVersion(): string {
  try {
    return execSync('git rev-parse --short HEAD', {
      stdio: ['ignore', 'pipe', 'ignore'],
    })
      .toString()
      .trim();
  } catch {
    // No git context (e.g. a source-tarball build). Fall back to a stable marker
    // so local builds don't thrash the persisted cache; production builds run in
    // a git checkout (CI and dev alike) and take the SHA path.
    return 'dev';
  }
}

export default defineConfig({
  plugins: [react(), tailwindcss()],
  define: {
    __BUILD_VERSION__: JSON.stringify(resolveBuildVersion()),
  },
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: process.env.services__api__http__0 || 'http://localhost:58343',
        changeOrigin: true,
      },
    },
  },
});
