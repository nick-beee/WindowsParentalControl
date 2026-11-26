/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// The same policy as the service sends for /admin and /request (AdminSpa.cs). `vite preview` uses it to check
// that the build runs without a CSP violation.
const contentSecurityPolicy =
  "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
  "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"

export default defineConfig({
  // The service serves index.html at /admin, request.html at /request (RequestSpa.cs) and the files of both pages
  // at /admin/assets/.
  base: '/admin/',
  // Two pages, one build: the web admin and the child request page share React and the build setup.
  // Vite resolves the paths relative to the project root.
  input: { admin: 'index.html', request: 'request.html' },
  plugins: [react()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // No inline data: URLs for scripts or styles: each asset is a file.
    assetsInlineLimit: 0,
  },
  server: {
    // `npm run dev` sends the API calls to a running service.
    proxy: { '/api': 'http://127.0.0.1:5050' },
  },
  preview: {
    headers: { 'Content-Security-Policy': contentSecurityPolicy },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    restoreMocks: true,
  },
})
