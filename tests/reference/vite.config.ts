import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

/** 仅用于离线 oracle 的隔离加载器；别名不会指向正在重构的生产客户端。 */
export default defineConfig({
  root: fileURLToPath(new URL('./apps/companion', import.meta.url)),
  resolve: { alias: { '@': fileURLToPath(new URL('./apps/companion/src', import.meta.url)) } },
  server: { middlewareMode: true, hmr: false, watch: null },
  appType: 'custom',
  logLevel: 'silent',
});
