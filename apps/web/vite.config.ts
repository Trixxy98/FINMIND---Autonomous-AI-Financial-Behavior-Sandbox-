import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // ADR-0013 item 14: loopback only, on a fixed port the API's Origin allowlist expects.
  server: { host: '127.0.0.1', port: 5173, strictPort: true },
});
