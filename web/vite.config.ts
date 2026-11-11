import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// In development the browser talks to one origin: Vite serves the app and forwards /api to the
// .NET API. The chat stream (server-sent events) passes through the same proxy.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
})
