import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

// Preview only: the real apps against a fixture of a simulated demo match (no .NET server). `npx vite -c vite.preview.config.ts`
export default defineConfig({
  plugins: [react()],
  resolve: { alias: { '@microsoft/signalr': fileURLToPath(new URL('./src/dev/fakeSignalr.ts', import.meta.url)) } },
  server: { port: 5199 },
});
