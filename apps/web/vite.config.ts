import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // ADR-0013 item 14: loopback only, on a fixed port the API's Origin allowlist expects.
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    // ADR-0014: the API sees the dev server's origin; Host becomes 127.0.0.1:5080.
    proxy: { '/api': { target: 'http://127.0.0.1:5080', changeOrigin: true } },
  },
});
