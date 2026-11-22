import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

const server = 'http://localhost:5000';

export default defineConfig({
  base: '/',
  plugins: [react()],
  build: { outDir: 'dist', assetsDir: 'assets' },
  server: {
    proxy: {
      '/api': server,
      '/admin': server,
      '/ui/config.json': server,
    },
  },
});
