import { execSync } from 'node:child_process'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'path'

function resolveGitCommitTime() {
  try {
    return execSync('git log -1 --date=format:"%Y-%m-%d %H:%M" --format=%cd')
      .toString()
      .trim()
  } catch {
    return 'unknown'
  }
}

const appVersion = resolveGitCommitTime()

export default defineConfig({
  root: __dirname,
  plugins: [react(), tailwindcss()],
  define: {
    __APP_COMMIT_HASH__: JSON.stringify(appVersion),
  },
  publicDir: path.resolve(__dirname, './public'),
  build: {
    outDir: path.resolve(__dirname, './dist'),
    emptyOutDir: true,
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
})
