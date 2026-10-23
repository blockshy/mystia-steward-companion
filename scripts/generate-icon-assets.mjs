import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Resvg } from '@resvg/resvg-js';

/**
 * 唯一图标源位于 assets/branding，与具体 UI 框架无关。
 * 锁定 resvg 渲染器直接生成 Windows 多帧 ICO 和 Android 启动图标；不执行 Tauri CLI，
 * 不访问字体或网络，不需要安装 Flutter/Android 即可检查资源是否过期。
 */
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sourcePath = 'assets/branding/icon-source.svg';
const source = await readFile(path.join(root, sourcePath), 'utf8');
const check = process.argv.includes('--check');
const viewport = source.match(/viewBox="0 0 (\d+) (\d+)"/);
const background = source.match(/data-android-background="(#[0-9a-f]{6})"/i)?.[1];
const references = [...source.matchAll(/\b(?:href|xlink:href)=["']([^"']*)["']/gi)];
if (!viewport || viewport[1] !== viewport[2] || !background ||
    /<(?:text|script|foreignObject|filter)\b|\burl\s*\(/i.test(source) ||
    references.some(([, value]) => !/^data:image\/png;base64,[A-Za-z0-9+/]+={0,2}$/.test(value))) {
  throw new Error('图标 SVG 必须是自包含的方形画布，禁止外部资源、文字、脚本和滤镜。');
}
const size = Number(viewport[1]);
const body = source.slice(source.indexOf('>', source.indexOf('<svg')) + 1, source.lastIndexOf('</svg>'));
const outputs = new Map();

function svg(content, dimension = size) {
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${dimension}" height="${dimension}" viewBox="0 0 ${dimension} ${dimension}">${content}</svg>`;
}
function png(document, width) {
  const image = new Resvg(document, { fitTo: { mode: 'width', value: width }, font: { loadSystemFonts: false } }).render();
  if (image.width !== width || image.height !== width) throw new Error('图标渲染尺寸不一致。');
  return image.asPng();
}
function ico(frames) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);
  let offset = 6 + frames.length * 16;
  const entries = frames.map(({ width, bytes }) => {
    const entry = Buffer.alloc(16);
    entry[0] = entry[1] = width === 256 ? 0 : width;
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(bytes.length, 8);
    entry.writeUInt32LE(offset, 12);
    offset += bytes.length;
    return entry;
  });
  return Buffer.concat([header, ...entries, ...frames.map((frame) => frame.bytes)]);
}

// Windows 大小包含高 DPI 标题栏/任务栏常用像素，两个 EXE 共用逐字节相同的 ICO。
const windowsIcon = ico([256, 128, 64, 48, 40, 32, 24, 20, 16].map((width) => ({ width, bytes: png(source, width) })));
outputs.set('apps/companion-flutter/windows/runner/resources/app_icon.ico', windowsIcon);
outputs.set('native/updater/resources/app_icon.ico', windowsIcon);

// 普通/圆形启动器分别保留 2/4 单位留白；自适应前景保持已验收的 0.38 安全区。
function legacy(round) {
  const margin = round ? 4 : 2;
  const shape = round ? '<circle cx="12" cy="12" r="11"/>' : '<rect x="2" y="2" width="20" height="20" rx="2"/>';
  return svg(`<defs><clipPath id="launcher-mask">${shape}</clipPath></defs><g clip-path="url(#launcher-mask)"><g transform="translate(${margin} ${margin}) scale(${(24 - margin * 2) / size})">${body}</g></g>`, 24);
}
const foreground = svg(`<g transform="translate(${size / 2} ${size / 2}) scale(0.38) translate(${-size / 2} ${-size / 2})">${body.replace(/<g\s+id="icon-background"[^>]*>/, '<g id="icon-background" display="none">')}</g>`);
const android = 'apps/companion-flutter/android/app/src/main/res';
for (const [density, width, adaptive] of [['mdpi', 48, 108], ['hdpi', 72, 162], ['xhdpi', 96, 216], ['xxhdpi', 144, 324], ['xxxhdpi', 192, 432]]) {
  outputs.set(`${android}/mipmap-${density}/ic_launcher.png`, png(legacy(false), width));
  outputs.set(`${android}/mipmap-${density}/ic_launcher_round.png`, png(legacy(true), width));
  outputs.set(`${android}/mipmap-${density}/ic_launcher_foreground.png`, png(foreground, adaptive));
}
const adaptiveXml = '<?xml version="1.0" encoding="utf-8"?>\n<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n  <background android:drawable="@color/ic_launcher_background"/>\n  <foreground android:drawable="@mipmap/ic_launcher_foreground"/>\n</adaptive-icon>\n';
outputs.set(`${android}/mipmap-anydpi-v26/ic_launcher.xml`, Buffer.from(adaptiveXml));
outputs.set(`${android}/mipmap-anydpi-v26/ic_launcher_round.xml`, Buffer.from(adaptiveXml));
outputs.set(`${android}/values/ic_launcher_background.xml`, Buffer.from(`<?xml version="1.0" encoding="utf-8"?>\n<resources><color name="ic_launcher_background">${background.toUpperCase()}</color></resources>\n`));

const manifest = await readFile(path.join(root, 'apps/companion-flutter/android/app/src/main/AndroidManifest.xml'), 'utf8');
if (!manifest.includes('android:icon="@mipmap/ic_launcher"') || !manifest.includes('android:roundIcon="@mipmap/ic_launcher_round"')) {
  throw new Error('Android manifest 未同时引用普通和圆形图标。');
}
const mismatches = [];
for (const [relative, bytes] of outputs) {
  const target = path.join(root, relative);
  if (check) {
    let current;
    try { current = await readFile(target); } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (!current?.equals(bytes)) mismatches.push(relative);
  } else {
    await mkdir(path.dirname(target), { recursive: true });
    await writeFile(target, bytes);
  }
}
if (mismatches.length) throw new Error(`图标资源过期，请执行 pnpm icons:generate：\n${mismatches.join('\n')}`);
console.log(JSON.stringify({ status: check ? 'verified' : 'generated', source: sourcePath,
  sourceSha256: createHash('sha256').update(source).digest('hex'), assets: outputs.size }));
