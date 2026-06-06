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
  plugins: [react(), tailwindcss()],
  define: {
    __APP_COMMIT_HASH__: JSON.stringify(appVersion),
  },
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
})
