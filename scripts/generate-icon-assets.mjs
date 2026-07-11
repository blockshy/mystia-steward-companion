import { spawnSync } from 'node:child_process';
import {
  access,
  copyFile,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateSync } from 'node:zlib';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sourcePath = path.join(projectRoot, 'apps/companion/src-tauri/icon-source.svg');
const iconDir = path.join(projectRoot, 'apps/companion/src-tauri/icons');
const androidRuntimeDir = path.join(
  projectRoot,
  'apps/companion/src-tauri/gen/android/app/src/main/res',
);
const androidManifestPath = path.join(
  projectRoot,
  'apps/companion/src-tauri/gen/android/app/src/main/AndroidManifest.xml',
);
const faviconPath = path.join(projectRoot, 'apps/companion/public/favicon.svg');
const tauriConfigPath = path.join(projectRoot, 'apps/companion/src-tauri/tauri.conf.json');
const tauriCliPath = path.join(projectRoot, 'node_modules/@tauri-apps/cli/tauri.js');
const checkOnly = process.argv.includes('--check');

const icoSizes = [256, 128, 64, 48, 40, 32, 24, 20, 16];
const icnsChunkOrder = [
  'is32',
  's8mk',
  'il32',
  'l8mk',
  'ic07',
  'ic08',
  'ic09',
  'ic10',
  'ic11',
  'ic12',
  'ic13',
  'ic14',
];
const androidForegroundScale = 0.65;
const androidForegroundMaxRadius = 104;

const desktopFiles = [
  '32x32.png',
  '64x64.png',
  '128x128.png',
  '128x128@2x.png',
  'Square30x30Logo.png',
  'Square44x44Logo.png',
  'Square71x71Logo.png',
  'Square89x89Logo.png',
  'Square107x107Logo.png',
  'Square142x142Logo.png',
  'Square150x150Logo.png',
  'Square284x284Logo.png',
  'Square310x310Logo.png',
  'StoreLogo.png',
  'icon.icns',
  'icon.ico',
  'icon.png',
  'tray-icon.png',
];

const iosPngSizes = new Map([
  ['ios/AppIcon-20x20@1x.png', 20],
  ['ios/AppIcon-20x20@2x-1.png', 40],
  ['ios/AppIcon-20x20@2x.png', 40],
  ['ios/AppIcon-20x20@3x.png', 60],
  ['ios/AppIcon-29x29@1x.png', 29],
  ['ios/AppIcon-29x29@2x-1.png', 58],
  ['ios/AppIcon-29x29@2x.png', 58],
  ['ios/AppIcon-29x29@3x.png', 87],
  ['ios/AppIcon-40x40@1x.png', 40],
  ['ios/AppIcon-40x40@2x-1.png', 80],
  ['ios/AppIcon-40x40@2x.png', 80],
  ['ios/AppIcon-40x40@3x.png', 120],
  ['ios/AppIcon-60x60@2x.png', 120],
  ['ios/AppIcon-60x60@3x.png', 180],
  ['ios/AppIcon-76x76@1x.png', 76],
  ['ios/AppIcon-76x76@2x.png', 152],
  ['ios/AppIcon-83.5x83.5@2x.png', 167],
  ['ios/AppIcon-512@2x.png', 1024],
]);

const androidDensities = [
  { name: 'mdpi', legacy: 48, foreground: 108 },
  { name: 'hdpi', legacy: 72, foreground: 162 },
  { name: 'xhdpi', legacy: 96, foreground: 216 },
  { name: 'xxhdpi', legacy: 144, foreground: 324 },
  { name: 'xxxhdpi', legacy: 192, foreground: 432 },
];
const androidPngSizes = new Map(
  androidDensities.flatMap(({ name, legacy, foreground }) => [
    [`android/mipmap-${name}/ic_launcher.png`, legacy],
    [`android/mipmap-${name}/ic_launcher_round.png`, legacy],
    [`android/mipmap-${name}/ic_launcher_foreground.png`, foreground],
  ]),
);
const tauriAndroidXmlFiles = [
  'android/mipmap-anydpi-v26/ic_launcher.xml',
  'android/values/ic_launcher_background.xml',
];
const generatedRoundXml = 'android/mipmap-anydpi-v26/ic_launcher_round.xml';
const tauriAndroidFiles = [...androidPngSizes.keys(), ...tauriAndroidXmlFiles];
const androidXmlFiles = [...tauriAndroidXmlFiles, generatedRoundXml];
const androidFiles = [...androidPngSizes.keys(), ...androidXmlFiles];
const generatedFiles = [...desktopFiles, ...iosPngSizes.keys(), ...androidFiles];

const expectedPngSizes = new Map([
  ['32x32.png', 32],
  ['64x64.png', 64],
  ['128x128.png', 128],
  ['128x128@2x.png', 256],
  ['Square30x30Logo.png', 30],
  ['Square44x44Logo.png', 44],
  ['Square71x71Logo.png', 71],
  ['Square89x89Logo.png', 89],
  ['Square107x107Logo.png', 107],
  ['Square142x142Logo.png', 142],
  ['Square150x150Logo.png', 150],
  ['Square284x284Logo.png', 284],
  ['Square310x310Logo.png', 310],
  ['StoreLogo.png', 50],
  ['icon.png', 512],
  ['tray-icon.png', 64],
  ...iosPngSizes,
  ...androidPngSizes,
]);

await access(sourcePath);
await access(tauriCliPath);
const sourceSvg = await readFile(sourcePath, 'utf8');
const source = validateSourceSvg(sourceSvg);
validateTauriIconConfig(JSON.parse(await readFile(tauriConfigPath, 'utf8')));
validateAndroidManifest(await readFile(androidManifestPath, 'utf8'));
const stagingRoot = await mkdtemp(path.join(tmpdir(), 'mystia-icon-assets-'));

try {
  const generatedDir = path.join(stagingRoot, 'generated');
  const frameDir = path.join(stagingRoot, 'frames');
  const appleDir = path.join(stagingRoot, 'apple');
  const androidDir = path.join(stagingRoot, 'android-platform');
  const legacySquareDir = path.join(stagingRoot, 'android-legacy-square');
  const legacyRoundDir = path.join(stagingRoot, 'android-legacy-round');
  const derivedDir = path.join(stagingRoot, 'derived');
  await Promise.all([
    generatedDir,
    frameDir,
    appleDir,
    androidDir,
    legacySquareDir,
    legacyRoundDir,
    derivedDir,
  ].map((directory) => mkdir(directory, { recursive: true })));

  const appleSourcePath = path.join(derivedDir, 'apple-source.svg');
  const androidSourcePath = path.join(derivedDir, 'android-source.svg');
  const androidForegroundPath = path.join(derivedDir, 'android-foreground.svg');
  const androidManifestPath = path.join(derivedDir, 'android-manifest.json');
  const legacySquarePath = path.join(derivedDir, 'android-legacy-square.svg');
  const legacyRoundPath = path.join(derivedDir, 'android-legacy-round.svg');

  await Promise.all([
    writeFile(appleSourcePath, buildOpaqueAppleSvg(source)),
    writeFile(androidSourcePath, sourceSvg),
    writeFile(androidForegroundPath, buildAndroidForegroundSvg(source)),
    writeFile(legacySquarePath, buildAndroidLegacySvg(source, false)),
    writeFile(legacyRoundPath, buildAndroidLegacySvg(source, true)),
  ]);
  await writeFile(
    androidManifestPath,
    `${JSON.stringify({
      default: 'android-source.svg',
      bg_color: source.androidBackgroundColor,
      android_fg: 'android-foreground.svg',
      android_fg_scale: 100,
    }, null, 2)}\n`,
  );

  runTauriIcon([sourcePath, '--output', generatedDir]);
  runTauriIcon([
    sourcePath,
    '--output',
    frameDir,
    ...icoSizes.flatMap((size) => ['--png', String(size)]),
  ]);
  runTauriIcon([appleSourcePath, '--output', appleDir, '--ios-color', source.backgroundColor]);
  runTauriIcon([androidManifestPath, '--output', androidDir]);

  const legacySizes = androidDensities.map(({ legacy }) => legacy);
  runTauriIcon([
    legacySquarePath,
    '--output',
    legacySquareDir,
    ...legacySizes.flatMap((size) => ['--png', String(size)]),
  ]);
  runTauriIcon([
    legacyRoundPath,
    '--output',
    legacyRoundDir,
    ...legacySizes.flatMap((size) => ['--png', String(size)]),
  ]);

  await copyFile(path.join(frameDir, '32x32.png'), path.join(generatedDir, '32x32.png'));
  await copyFile(path.join(frameDir, '64x64.png'), path.join(generatedDir, '64x64.png'));
  await copyFile(path.join(frameDir, '128x128.png'), path.join(generatedDir, '128x128.png'));
  await copyFile(path.join(frameDir, '256x256.png'), path.join(generatedDir, '128x128@2x.png'));
  await copyFile(path.join(frameDir, '64x64.png'), path.join(generatedDir, 'tray-icon.png'));

  const icoFrames = await Promise.all(
    icoSizes.map(async (size) => ({
      size,
      png: await readFile(path.join(frameDir, `${size}x${size}.png`)),
    })),
  );
  await writeFile(path.join(generatedDir, 'icon.ico'), buildIco(icoFrames));
  await writeFile(
    path.join(generatedDir, 'icon.icns'),
    canonicalizeIcns(await readFile(path.join(generatedDir, 'icon.icns'))),
  );

  await copyRelativeFiles([...iosPngSizes.keys()], appleDir, generatedDir);
  await copyRelativeFiles(tauriAndroidFiles, androidDir, generatedDir);
  await copyFile(
    path.join(generatedDir, 'android/mipmap-anydpi-v26/ic_launcher.xml'),
    path.join(generatedDir, generatedRoundXml),
  );
  for (const { name, legacy } of androidDensities) {
    await copyFile(
      path.join(legacySquareDir, `${legacy}x${legacy}.png`),
      path.join(generatedDir, `android/mipmap-${name}/ic_launcher.png`),
    );
    await copyFile(
      path.join(legacyRoundDir, `${legacy}x${legacy}.png`),
      path.join(generatedDir, `android/mipmap-${name}/ic_launcher_round.png`),
    );
  }

  await validateGeneratedAssets(generatedDir, icoFrames, source.androidBackgroundColor);

  const mismatches = [];
  for (const file of generatedFiles) {
    await syncOrCompare(
      path.join(generatedDir, file),
      path.join(iconDir, file),
      `apps/companion/src-tauri/icons/${file}`,
      mismatches,
    );
  }
  for (const file of androidFiles) {
    const relativeAndroidPath = file.slice('android/'.length);
    await syncOrCompare(
      path.join(generatedDir, file),
      path.join(androidRuntimeDir, relativeAndroidPath),
      `apps/companion/src-tauri/gen/android/app/src/main/res/${relativeAndroidPath}`,
      mismatches,
    );
  }

  if (checkOnly) {
    if (!(await filesEqual(sourcePath, faviconPath))) {
      mismatches.push('apps/companion/public/favicon.svg');
    }
  } else {
    await copyFile(sourcePath, faviconPath);
  }

  if (mismatches.length > 0) {
    throw new Error(
      `icon assets are stale:\n${mismatches.map((file) => `- ${file}`).join('\n')}\nRun pnpm icons:generate.`,
    );
  }

  console.log(checkOnly ? 'All icon assets match the canonical SVG.' : 'Generated all icon assets.');
} finally {
  await rm(stagingRoot, { recursive: true, force: true });
}

function runTauriIcon(args) {
  const result = spawnSync(process.execPath, [tauriCliPath, 'icon', ...args], {
    cwd: projectRoot,
    stdio: 'inherit',
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(`tauri icon failed with exit code ${result.status ?? 'unknown'}`);
  }
}

function validateSourceSvg(svg) {
  if (svg.includes('\r')) {
    throw new Error('icon-source.svg must use LF line endings');
  }
  const root = svg.match(/<svg\b[^>]*>/i);
  const viewBox = root?.[0].match(
    /\bviewBox=["']\s*([\d.-]+)\s+([\d.-]+)\s+([\d.-]+)\s+([\d.-]+)\s*["']/,
  );
  const x = Number(viewBox?.[1]);
  const y = Number(viewBox?.[2]);
  const width = Number(viewBox?.[3]);
  const height = Number(viewBox?.[4]);
  if (!viewBox || width <= 0 || width !== height) {
    throw new Error('icon-source.svg must have a positive square viewBox');
  }
  if (/<(?:image|text|script|foreignObject|filter)\b|\b(?:href|xlink:href)=|\burl\s*\(/i.test(svg)) {
    throw new Error('icon-source.svg must be a self-contained vector without images, fonts, scripts, or filters');
  }
  const backgroundColor = root[0].match(/\bdata-icon-background=["'](#[0-9a-f]{6})["']/i)?.[1];
  if (!backgroundColor) {
    throw new Error('icon-source.svg must define data-icon-background="#RRGGBB"');
  }
  const androidBackgroundColor = root[0].match(
    /\bdata-android-background=["'](#[0-9a-f]{6})["']/i,
  )?.[1];
  if (!androidBackgroundColor) {
    throw new Error('icon-source.svg must define data-android-background="#RRGGBB"');
  }
  for (const layerId of ['icon-background', 'icon-foreground']) {
    const matches = [...svg.matchAll(new RegExp(`\\bid=["']${layerId}["']`, 'g'))];
    if (matches.length !== 1) {
      throw new Error(`icon-source.svg must define exactly one ${layerId} layer`);
    }
  }
  const closingTagOffset = svg.lastIndexOf('</svg>');
  if (!root || root.index === undefined || closingTagOffset < root.index + root[0].length) {
    throw new Error('icon-source.svg has an invalid root element');
  }
  return {
    androidBackgroundColor: androidBackgroundColor.toUpperCase(),
    backgroundColor: backgroundColor.toUpperCase(),
    body: svg.slice(root.index + root[0].length, closingTagOffset).trim(),
    height,
    width,
    x,
    y,
  };
}

function validateTauriIconConfig(config) {
  const configuredIcons = config?.bundle?.icon;
  if (!Array.isArray(configuredIcons)) {
    throw new Error('tauri.conf.json bundle.icon must be an array');
  }
  const expectedIcons = [
    'icons/32x32.png',
    'icons/64x64.png',
    'icons/128x128.png',
    'icons/128x128@2x.png',
    'icons/icon.png',
    'icons/icon.icns',
    'icons/icon.ico',
  ];
  if (configuredIcons.join(',') !== expectedIcons.join(',')) {
    throw new Error(`tauri.conf.json bundle.icon must be ${expectedIcons.join(',')}`);
  }
}

function validateAndroidManifest(manifest) {
  const applicationTag = manifest.match(/<application\b[^>]*>/s)?.[0];
  if (!applicationTag) throw new Error('AndroidManifest.xml must define an application element');
  const expectedAttributes = new Map([
    ['android:icon', '@mipmap/ic_launcher'],
    ['android:roundIcon', '@mipmap/ic_launcher_round'],
  ]);
  for (const [attribute, expectedValue] of expectedAttributes) {
    const actualValue = applicationTag.match(new RegExp(`\\b${attribute}=["']([^"']+)["']`))?.[1];
    if (actualValue !== expectedValue) {
      throw new Error(`AndroidManifest.xml ${attribute} must be ${expectedValue}`);
    }
  }
}

function buildSvg(source, content, viewport = source) {
  return `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" width="${viewport.width}" height="${viewport.height}" viewBox="${viewport.x} ${viewport.y} ${viewport.width} ${viewport.height}" fill="none" shape-rendering="geometricPrecision">
${content}
</svg>
`;
}

function buildOpaqueAppleSvg(source) {
  const background = `<rect x="${source.x}" y="${source.y}" width="${source.width}" height="${source.height}" fill="${source.backgroundColor}"/>`;
  return buildSvg(source, `  ${background}\n${source.body}`);
}

function buildAndroidForegroundSvg(source) {
  const backgroundGroupPattern = /<g\b(?=[^>]*\bid=["']icon-background["'])[^>]*>/i;
  const hiddenBackground = source.body.replace(
    backgroundGroupPattern,
    (openingTag) => `${openingTag.slice(0, -1)} display="none">`,
  );
  if (hiddenBackground === source.body) {
    throw new Error('icon-background must be a group so the adaptive foreground can hide it');
  }
  const centerX = source.x + source.width / 2;
  const centerY = source.y + source.height / 2;
  const transform = [
    `translate(${centerX} ${centerY})`,
    `scale(${androidForegroundScale})`,
    `translate(${-centerX} ${-centerY})`,
  ].join(' ');
  return buildSvg(source, `  <g transform="${transform}">\n${hiddenBackground}\n  </g>`);
}

function buildAndroidLegacySvg(source, round) {
  const canvasSize = 24;
  const margin = round ? 1 : 2;
  const innerSize = canvasSize - margin * 2;
  const scaleX = innerSize / source.width;
  const scaleY = innerSize / source.height;
  const shape = round
    ? '<circle cx="12" cy="12" r="11"/>'
    : '<rect x="2" y="2" width="20" height="20" rx="2" ry="2"/>';
  const transform = [
    `translate(${margin} ${margin})`,
    `scale(${scaleX} ${scaleY})`,
    `translate(${-source.x} ${-source.y})`,
  ].join(' ');
  const content = `  <defs>
    <clipPath id="legacy-mask">${shape}</clipPath>
  </defs>
  <g clip-path="url(#legacy-mask)">
    <g transform="${transform}">
${source.body}
    </g>
  </g>`;
  return buildSvg(source, content, {
    x: 0,
    y: 0,
    width: canvasSize,
    height: canvasSize,
  });
}

function buildIco(frames) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(frames.length, 4);

  let imageOffset = 6 + frames.length * 16;
  const entries = frames.map(({ size, png }) => {
    const entry = Buffer.alloc(16);
    entry[0] = size === 256 ? 0 : size;
    entry[1] = size === 256 ? 0 : size;
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(png.length, 8);
    entry.writeUInt32LE(imageOffset, 12);
    imageOffset += png.length;
    return entry;
  });

  return Buffer.concat([header, ...entries, ...frames.map(({ png }) => png)]);
}

function canonicalizeIcns(buffer) {
  const chunks = readIcnsChunks(buffer);
  const chunksByType = new Map(chunks.map((chunk) => [chunk.type, chunk.data]));
  if (chunksByType.size !== chunks.length) {
    throw new Error('icon.icns contains duplicate chunk types');
  }
  const actualTypes = [...chunksByType.keys()].sort();
  const expectedTypes = [...icnsChunkOrder].sort();
  if (actualTypes.join(',') !== expectedTypes.join(',')) {
    throw new Error(`icon.icns chunks must be ${expectedTypes.join(',')}, got ${actualTypes.join(',')}`);
  }
  const orderedChunks = icnsChunkOrder.map((type) => {
    const data = chunksByType.get(type);
    const header = Buffer.alloc(8);
    header.write(type, 0, 4, 'ascii');
    header.writeUInt32BE(data.length + 8, 4);
    return Buffer.concat([header, data]);
  });
  const header = Buffer.alloc(8);
  header.write('icns', 0, 4, 'ascii');
  header.writeUInt32BE(8 + orderedChunks.reduce((length, chunk) => length + chunk.length, 0), 4);
  return Buffer.concat([header, ...orderedChunks]);
}

function readIcnsChunks(buffer) {
  if (buffer.length < 8 || buffer.toString('ascii', 0, 4) !== 'icns') {
    throw new Error('icon.icns has an invalid header');
  }
  if (buffer.readUInt32BE(4) !== buffer.length) {
    throw new Error('icon.icns has an invalid declared length');
  }
  const chunks = [];
  let offset = 8;
  while (offset < buffer.length) {
    if (offset + 8 > buffer.length) throw new Error('icon.icns has a truncated chunk header');
    const type = buffer.toString('ascii', offset, offset + 4);
    const length = buffer.readUInt32BE(offset + 4);
    if (length < 8 || offset + length > buffer.length) {
      throw new Error(`icon.icns has an invalid ${type} chunk`);
    }
    chunks.push({ type, data: buffer.subarray(offset + 8, offset + length) });
    offset += length;
  }
  return chunks;
}

async function validateGeneratedAssets(generatedDir, icoFrames, androidBackgroundColor) {
  for (const [file, expectedSize] of expectedPngSizes) {
    const png = await readFile(path.join(generatedDir, file));
    const info = readPngInfo(png, file);
    if (info.width !== expectedSize || info.height !== expectedSize) {
      throw new Error(`${file} must be ${expectedSize}x${expectedSize}, got ${info.width}x${info.height}`);
    }
    if (info.bitDepth !== 8 || info.colorType !== 6 || info.interlace !== 0) {
      throw new Error(`${file} must be a non-interlaced 8-bit RGBA PNG`);
    }
    if (file.startsWith('ios/')) {
      const pixels = decodeRgbaPng(png, file);
      if (pixels.minAlpha !== 255) {
        throw new Error(`${file} must be fully opaque, got minimum alpha ${pixels.minAlpha}`);
      }
    }
  }

  const ico = await readFile(path.join(generatedDir, 'icon.ico'));
  const entries = readIcoEntries(ico);
  const actualSizes = entries.map(({ width }) => width);
  if (actualSizes.join(',') !== icoSizes.join(',')) {
    throw new Error(`ICO frame order must be ${icoSizes.join(',')}, got ${actualSizes.join(',')}`);
  }
  for (let index = 0; index < entries.length; index += 1) {
    const entry = entries[index];
    const embeddedPng = ico.subarray(entry.offset, entry.offset + entry.length);
    const info = readPngInfo(embeddedPng, `icon.ico frame ${entry.width}`);
    if (
      info.width !== entry.width
      || info.height !== entry.height
      || info.bitDepth !== 8
      || info.colorType !== 6
      || entry.bitsPerPixel !== 32
    ) {
      throw new Error(`invalid ICO frame ${entry.width}x${entry.height}`);
    }
    if (!embeddedPng.equals(icoFrames[index].png)) {
      throw new Error(`ICO frame ${entry.width} does not match its direct SVG render`);
    }
  }

  const icns = await readFile(path.join(generatedDir, 'icon.icns'));
  if (!icns.equals(canonicalizeIcns(icns))) {
    throw new Error('icon.icns chunks are not in canonical order');
  }

  const adaptiveForeground = decodeRgbaPng(
    await readFile(path.join(generatedDir, 'android/mipmap-xxxhdpi/ic_launcher_foreground.png')),
    'android adaptive foreground',
  );
  const safeRadius = 132;
  if (adaptiveForeground.nonTransparentPixels === 0) {
    throw new Error('Android adaptive foreground must not be empty');
  }
  if (adaptiveForeground.maxRadiusSquared > safeRadius ** 2) {
    throw new Error(
      `Android adaptive foreground must fit the 66/108 circular safe area, got radius ${Math.sqrt(adaptiveForeground.maxRadiusSquared).toFixed(2)}`,
    );
  }
  if (adaptiveForeground.maxRadiusSquared > androidForegroundMaxRadius ** 2) {
    throw new Error(
      `Android adaptive foreground must preserve launcher padding, got radius ${Math.sqrt(adaptiveForeground.maxRadiusSquared).toFixed(2)}`,
    );
  }

  for (const file of ['ic_launcher.xml', 'ic_launcher_round.xml']) {
    const adaptiveXml = await readFile(
      path.join(generatedDir, `android/mipmap-anydpi-v26/${file}`),
      'utf8',
    );
    if (
      !adaptiveXml.includes('@mipmap/ic_launcher_foreground')
      || !adaptiveXml.includes('@color/ic_launcher_background')
    ) {
      throw new Error(`${file} must reference the generated adaptive foreground and background`);
    }
  }
  const backgroundXml = await readFile(
    path.join(generatedDir, 'android/values/ic_launcher_background.xml'),
    'utf8',
  );
  if (!backgroundXml.includes(`<color name="ic_launcher_background">${androidBackgroundColor}</color>`)) {
    throw new Error(`Android adaptive background must be ${androidBackgroundColor}`);
  }
}

function readPngInfo(buffer, label) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  if (buffer.length < 33 || !buffer.subarray(0, 8).equals(signature)) {
    throw new Error(`${label} is not a PNG`);
  }
  if (buffer.toString('ascii', 12, 16) !== 'IHDR') {
    throw new Error(`${label} is missing PNG IHDR`);
  }
  return {
    width: buffer.readUInt32BE(16),
    height: buffer.readUInt32BE(20),
    bitDepth: buffer[24],
    colorType: buffer[25],
    interlace: buffer[28],
  };
}

function decodeRgbaPng(buffer, label) {
  const info = readPngInfo(buffer, label);
  if (info.bitDepth !== 8 || info.colorType !== 6 || info.interlace !== 0) {
    throw new Error(`${label} cannot be decoded as a non-interlaced 8-bit RGBA PNG`);
  }
  const idatChunks = [];
  let offset = 8;
  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('ascii', offset + 4, offset + 8);
    if (type === 'IDAT') idatChunks.push(buffer.subarray(offset + 8, offset + 8 + length));
    offset += length + 12;
  }
  const encoded = inflateSync(Buffer.concat(idatChunks));
  const bytesPerPixel = 4;
  const stride = info.width * bytesPerPixel;
  const decoded = Buffer.alloc(info.height * stride);
  let encodedOffset = 0;
  for (let row = 0; row < info.height; row += 1) {
    const filter = encoded[encodedOffset];
    encodedOffset += 1;
    if (filter > 4) throw new Error(`${label} uses unsupported PNG filter ${filter}`);
    for (let column = 0; column < stride; column += 1) {
      const current = encoded[encodedOffset];
      encodedOffset += 1;
      const left = column >= bytesPerPixel ? decoded[row * stride + column - bytesPerPixel] : 0;
      const above = row > 0 ? decoded[(row - 1) * stride + column] : 0;
      const upperLeft = row > 0 && column >= bytesPerPixel
        ? decoded[(row - 1) * stride + column - bytesPerPixel]
        : 0;
      const predictor = filter === 1
        ? left
        : filter === 2
          ? above
          : filter === 3
            ? Math.floor((left + above) / 2)
            : filter === 4
              ? paethPredictor(left, above, upperLeft)
              : 0;
      decoded[row * stride + column] = (current + predictor) & 0xff;
    }
  }

  let minAlpha = 255;
  let maxRadiusSquared = 0;
  let nonTransparentPixels = 0;
  for (let y = 0; y < info.height; y += 1) {
    for (let x = 0; x < info.width; x += 1) {
      const alpha = decoded[y * stride + x * bytesPerPixel + 3];
      minAlpha = Math.min(minAlpha, alpha);
      if (alpha > 0) {
        nonTransparentPixels += 1;
        const deltaX = x + 0.5 - info.width / 2;
        const deltaY = y + 0.5 - info.height / 2;
        maxRadiusSquared = Math.max(maxRadiusSquared, deltaX ** 2 + deltaY ** 2);
      }
    }
  }
  return {
    maxRadiusSquared,
    minAlpha,
    nonTransparentPixels,
  };
}

function paethPredictor(left, above, upperLeft) {
  const prediction = left + above - upperLeft;
  const leftDistance = Math.abs(prediction - left);
  const aboveDistance = Math.abs(prediction - above);
  const upperLeftDistance = Math.abs(prediction - upperLeft);
  if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance) return left;
  return aboveDistance <= upperLeftDistance ? above : upperLeft;
}

function readIcoEntries(buffer) {
  if (buffer.length < 6 || buffer.readUInt16LE(0) !== 0 || buffer.readUInt16LE(2) !== 1) {
    throw new Error('icon.ico has an invalid header');
  }
  const count = buffer.readUInt16LE(4);
  if (buffer.length < 6 + count * 16) throw new Error('icon.ico directory is truncated');
  return Array.from({ length: count }, (_, index) => {
    const offset = 6 + index * 16;
    return {
      width: buffer[offset] || 256,
      height: buffer[offset + 1] || 256,
      bitsPerPixel: buffer.readUInt16LE(offset + 6),
      length: buffer.readUInt32LE(offset + 8),
      offset: buffer.readUInt32LE(offset + 12),
    };
  });
}

async function copyRelativeFiles(files, sourceDir, targetDir) {
  for (const file of files) {
    const targetPath = path.join(targetDir, file);
    await mkdir(path.dirname(targetPath), { recursive: true });
    await copyFile(path.join(sourceDir, file), targetPath);
  }
}

async function syncOrCompare(generatedPath, targetPath, label, mismatches) {
  if (checkOnly) {
    if (!(await filesEqual(generatedPath, targetPath))) mismatches.push(label);
    return;
  }
  await mkdir(path.dirname(targetPath), { recursive: true });
  await copyFile(generatedPath, targetPath);
}

async function filesEqual(leftPath, rightPath) {
  try {
    const [left, right] = await Promise.all([readFile(leftPath), readFile(rightPath)]);
    return left.equals(right);
  } catch (error) {
    if (error?.code === 'ENOENT') return false;
    throw error;
  }
}
