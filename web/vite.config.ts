import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// `npm run dev` serves the UI on :5173 and forwards the API and the SignalR hub to the .NET server on :5080.
// `npm run build` writes the production bundle into the server's wwwroot, so one process serves everything.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5080',
      '/hubs': { target: 'http://localhost:5080', ws: true },
    },
  },
  build: {
    outDir: '../src/MatchLens.Server/wwwroot',
    emptyOutDir: true,
  },
});
