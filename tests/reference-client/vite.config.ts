import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

/** 仅离线差分使用此加载器；不监听端口，不指向已删除的生产 TS 应用。 */
export default defineConfig({
  root: fileURLToPath(new URL('.', import.meta.url)),
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: { middlewareMode: true, hmr: false, watch: null },
  appType: 'custom',
  logLevel: 'silent',
});
