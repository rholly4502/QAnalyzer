import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Base-aware: subpath /analyzer/ per arsitektur deploy rhol-ai.tech.
export default defineConfig({
  base: '/analyzer/',
  plugins: [react()],
  server: {
    proxy: {
      '/analyzer/api': {
        target: 'http://localhost:5213',
        changeOrigin: true,
        rewrite: (p) => p.replace(/^\/analyzer/, ''),
      },
    },
  },
  build: {
    outDir: '../src/QAnalyzer.Web/wwwroot',
    emptyOutDir: true,
  },
});
