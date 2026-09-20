import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { createServer, transformWithOxc } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { chromium } from 'playwright';
const output = process.env.THEME_CONTRAST_OUTPUT_DIR || '/tmp/mystia-theme-contrast';
await mkdir(output, { recursive: true });
const fixture = `
import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import '@mantine/core/styles.css';
import '@/index.css';
import { CompanionMantineProvider } from '@/components/ui/mantine-provider';
import { setStoredThemeMode } from '@/lib/theme';
import {
  Badge,
  Button,
  Card,
  CardContent,
  Dialog,
  Input,
  SelectBox,
  SegmentedControl,
  Slider,
  SwitchField,
  Tabs,
  TabsList,
  TabsTrigger,
  TabsContent,
  SettingHelpProvider,
  SettingHelpField,
  StatusCard,
} from '@/components/ui-kit';
function Fixture() {
  const [checked, setChecked] = useState(false),
    [value, setValue] = useState(40),
    [open, setOpen] = useState(false);
  window.setFixtureTheme = setStoredThemeMode;
  return (
    <CompanionMantineProvider>
      <SettingHelpProvider>
        <main className="companion-shell min-h-screen space-y-4 p-4">
          <p>页面正文</p>
          <p className="text-muted-foreground">页面辅助文字</p>
          <Card>
            <CardContent className="space-y-4">
              <p>卡片正文</p>
              <p className="text-muted-foreground">辅助文字</p>
              <p className="text-success">连接已确认</p>
              <p className="text-warning">状态需要注意</p>
              <p className="text-destructive">请求失败</p>
              <div className="flex flex-wrap gap-2">
                {['default', 'secondary', 'outline', 'destructive', 'ghost'].map((variant) => (
                  <Badge key={variant} variant={variant}>
                    {variant}徽标
                  </Badge>
                ))}
              </div>
              <div className="flex flex-wrap gap-2">
                {['preferred', 'disliked', 'cancelled', 'extra', 'matched', 'default'].map((tone) => (
                  <span key={tone} className={'steward-tag-' + tone}>
                    {tone}标签
                  </span>
                ))}
              </div>
              <div className="flex flex-wrap gap-2">
                {['default', 'secondary', 'outline', 'destructive', 'ghost', 'link'].map((variant) => (
                  <Button key={variant} variant={variant} data-probe={'button-' + variant}>
                    {variant}按钮
                  </Button>
                ))}
                <Button disabled data-probe="disabled-button">
                  禁用按钮
                </Button>
              </div>
              <Input aria-label="输入字段" placeholder="输入名称" data-probe="input" />
              <Input aria-label="只读字段" readOnly value="已确认只读值" />
              <Input aria-label="禁用字段" disabled value="暂不可修改" data-probe="disabled-input" />
              <SelectBox
                aria-label="选择字段"
                value="one"
                onValueChange={() => {}}
                options={[
                  { value: 'one', label: '选中项目' },
                  { value: 'two', label: '普通项目' },
                ]}
              />
              <SegmentedControl
                value="one"
                onValueChange={() => {}}
                options={[
                  { value: 'one', label: '已选分组' },
                  { value: 'two', label: '其他分组' },
                ]}
              />
              <SwitchField label="开关" checked={checked} onCheckedChange={setChecked} />
              <SwitchField label="禁用开关" checked={false} onCheckedChange={() => {}} disabled />
              <Slider aria-label="数值调整" value={value} onValueChange={setValue} />
              <Slider aria-label="禁用数值" value={20} onValueChange={() => {}} disabled />
              <Tabs defaultValue="one">
                <TabsList>
                  <TabsTrigger value="one">当前页签</TabsTrigger>
                  <TabsTrigger value="two">其他页签</TabsTrigger>
                </TabsList>
                <TabsContent value="one">页签内容</TabsContent>
                <TabsContent value="two">其他内容</TabsContent>
              </Tabs>
              <SettingHelpField id="contrast-help" label="设置项" description="帮助浮层的可读正文">
                {({ helpTrigger }) => <div className="flex items-center">设置项{helpTrigger}</div>}
              </SettingHelpField>
              <Button data-gamepad-focus-key="contrast:dialog" onClick={() => setOpen(true)}>
                打开确认
              </Button>
            </CardContent>
          </Card>
          <StatusCard label="连接" value="已连接" detail="连接信息已确认" tone="good" />
          <Dialog
            title="确认操作"
            opened={open}
            onClose={() => setOpen(false)}
            returnFocusKey="contrast:dialog"
          >
            <p>确认正文与表面保持实色。</p>
            <p className="text-muted-foreground">操作的解释文字</p>
            <Button onClick={() => setOpen(false)}>关闭确认</Button>
          </Dialog>
        </main>
      </SettingHelpProvider>
    </CompanionMantineProvider>
  );
}
createRoot(document.getElementById('root')).render(<Fixture />);
`;
const server = await createServer({
  configFile: false,
  root: process.cwd(),
  cacheDir: path.join(output, 'vite-cache'),
  logLevel: 'error',
  resolve: {
    alias: { '@': path.resolve('apps/companion/src') },
    dedupe: ['react', 'react-dom'],
  },
  optimizeDeps: {
    include: ['react', 'react-dom/client', '@mantine/core', '@mantine/hooks', '@tabler/icons-react'],
  },
  server: { host: '127.0.0.1', port: 0, hmr: false, watch: null },
  plugins: [
    react(),
    tailwindcss(),
    {
      name: 'theme-contrast-fixture',
      resolveId(id) {
        if (id === '/theme-contrast.jsx') return '\0theme-contrast.jsx';
      },
      async load(id) {
        if (id === '\0theme-contrast.jsx')
          return (await transformWithOxc(fixture, 'theme-contrast.jsx')).code;
      },
      configureServer(vite) {
        vite.middlewares.use(async (req, res, next) => {
          if (req.url !== '/') return next();
          res.setHeader('content-type', 'text/html');
          res.end(
            await vite.transformIndexHtml(
              '/',
              '<html lang="zh-CN"><body><div id="root"></div><script type="module" src="/theme-contrast.jsx"></script></body></html>',
            ),
          );
        });
      },
    },
  ],
});
await server.listen();
const browser = await chromium.launch({
  headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH
    ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH }
    : {}),
});
const reports = [];
const scenarios = ['light', 'dark'].flatMap((theme) => [
  ...[1280, 640, 390].flatMap((width) =>
    [90, 100, 130].map((scale) => ({
      theme,
      width,
      scale,
      opacity: 100,
      backdrop: '#fff',
    })),
  ),
  ...['#fff', '#000'].map((backdrop) => ({
    theme,
    width: 640,
    scale: 100,
    opacity: 96,
    backdrop,
  })),
]);
try {
  for (const { theme, width, scale, opacity, backdrop } of scenarios) {
    const page = await browser.newPage({ viewport: { width, height: 1000 } });
    const errors = [];
    page.on('pageerror', (error) => errors.push(error.stack || String(error)));
    await page.addInitScript(
      ({ theme, scale, opacity, backdrop }) => {
        localStorage.setItem('mystia-steward-companion-theme-mode', theme);
        document.addEventListener(
          'DOMContentLoaded',
          () => {
            document.documentElement.style.setProperty('--companion-font-scale', String(scale / 100));
            document.documentElement.style.setProperty(
              '--companion-background-opacity-percent',
              opacity + '%',
            );
            document.documentElement.style.backgroundColor = backdrop;
          },
          { once: true },
        );
      },
      { theme, scale, opacity, backdrop },
    );
    await page.goto('http://127.0.0.1:' + server.httpServer.address().port);
    await page.getByRole('button', { name: '打开确认', exact: true }).waitFor();
    await page.waitForFunction(
      (theme) => document.documentElement.classList.contains('dark') === (theme === 'dark'),
      theme,
    );
    const snapshots = [];
    async function inspect(state) {
      await page.evaluate(async () => {
        document.body.getBoundingClientRect();
        await Promise.all(
          document
            .getAnimations()
            .filter((animation) => animation.effect?.getTiming().iterations !== Infinity)
            .map((animation) => animation.finished.catch(() => {})),
        );
      });
      snapshots.push({ state, ...(await page.evaluate(measureContrast)) });
    }
    await inspect('default');
    await page.getByRole('switch', { name: '开关', exact: true }).focus();
    await page.keyboard.press('Space');
    await inspect('checked');
    await page.getByRole('slider', { name: '数值调整', exact: true }).focus();
    await page.getByRole('slider', { name: '数值调整', exact: true }).hover();
    await inspect('slider-focus-hover');
    await page.getByRole('radio', { name: '已选分组', exact: true }).focus();
    await page.getByText('已选分组', { exact: true }).hover();
    await inspect('segmented-focus-hover');
    for (const variant of ['default', 'outline', 'destructive']) {
      await page.locator('[data-probe="button-' + variant + '"]').hover();
      await inspect('hover-' + variant);
    }
    await page.keyboard.press('Tab');
    await page.locator('[data-probe="input"]').focus();
    await inspect('focus');
    await page.getByRole('combobox', { name: '选择字段', exact: true }).click();
    await page.getByRole('option', { name: '普通项目', exact: true }).waitFor();
    await inspect('select-open');
    await page.keyboard.press('Escape');
    await page.locator('[data-setting-help-trigger]').hover();
    await page.getByRole('tooltip').waitFor();
    await inspect('tooltip');
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: '打开确认', exact: true }).click();
    await page.getByRole('dialog').waitFor();
    await inspect('dialog');
    await page.getByRole('button', { name: '关闭确认', exact: true }).click();
    const report = {
      theme,
      width,
      scale,
      opacity,
      backdrop,
      snapshots,
      errors,
    };
    reports.push(report);
    const failures = snapshots.flatMap((snapshot) =>
      snapshot.failures.map((failure) => ({
        state: snapshot.state,
        ...failure,
      })),
    );
    if (failures.length) console.error(JSON.stringify({ theme, width, scale, failures }, null, 2));
    assert.equal(failures.length, 0, 'Rendered contrast thresholds failed.');
    assert.deepEqual(errors, []);
    assert(
      snapshots.every((snapshot) => !snapshot.overflow),
      'Fixture has horizontal page overflow.',
    );
    if (scale === 130)
      await page.screenshot({
        path: path.join(output, theme + '-' + width + '-' + scale + '.png'),
        fullPage: true,
      });
    await page.close();
  }
  console.log(
    JSON.stringify(
      {
        passed: true,
        combinations: reports.length,
        states: reports.reduce((sum, r) => sum + r.snapshots.length, 0),
        output,
      },
      null,
      2,
    ),
  );
} finally {
  await writeFile(path.join(output, 'report.json'), JSON.stringify(reports, null, 2));
  await browser.close();
  await server.close();
}
// Colors are computed from actual rendered components, including each translucent ancestor.
function measureContrast() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 1;
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  const color = (value) => {
    ctx.clearRect(0, 0, 1, 1);
    ctx.fillStyle = value;
    ctx.fillRect(0, 0, 1, 1);
    const d = ctx.getImageData(0, 0, 1, 1).data;
    return [d[0], d[1], d[2], d[3] / 255];
  };
  const blend = (a, b) => [...a.slice(0, 3).map((v, i) => v * a[3] + b[i] * (1 - a[3])), 1];
  const luminance = (a) =>
    a
      .slice(0, 3)
      .map((v) => v / 255)
      .map((v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4))
      .reduce((sum, v, i) => sum + v * [0.2126, 0.7152, 0.0722][i], 0);
  const contrast = (a, b) => {
    const x = luminance(a),
      y = luminance(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
  };
  const background = (el) => {
    const ancestors = [];
    for (let n = el; n; n = n.parentElement) ancestors.unshift(n);
    return ancestors.reduce(
      (bg, n) => blend(color(getComputedStyle(n).backgroundColor), bg),
      [255, 255, 255, 1],
    );
  };
  const checks = [],
    failures = [];
  const check = (kind, label, ratio, min) => {
    const entry = { kind, label, ratio: Number(ratio.toFixed(3)), min };
    checks.push(entry);
    if (ratio + 0.015 < min) failures.push(entry);
  };
  for (const el of document.querySelectorAll('body *')) {
    if (
      !(el instanceof HTMLElement) ||
      !el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true }) ||
      el.closest('[disabled],[aria-disabled="true"],[data-disabled="true"],.steward-setting-help-sr-only')
    )
      continue;
    const style = getComputedStyle(el),
      bg = background(el),
      outside = background(el.parentElement);
    const text = [...el.childNodes]
      .filter((node) => node.nodeType === 3)
      .map((node) => node.textContent)
      .join('')
      .trim();
    if (text) {
      const large =
        parseFloat(style.fontSize) >= 24 ||
        (parseFloat(style.fontSize) >= 18.66 && Number(style.fontWeight) >= 700);
      check('text', text, contrast(blend(color(style.color), bg), bg), large ? 3 : 4.5);
    }
    if (el.matches('input:not([type="hidden"]):not([type="checkbox"])')) {
      if (el.placeholder)
        check(
          'placeholder',
          el.placeholder,
          contrast(blend(color(getComputedStyle(el, '::placeholder').color), bg), bg),
          4.5,
        );
      if (el.value)
        check('value', el.getAttribute('aria-label'), contrast(blend(color(style.color), bg), bg), 4.5);
      check(
        'control-border',
        el.getAttribute('aria-label'),
        Math.min(
          contrast(blend(color(style.borderTopColor), bg), bg),
          contrast(blend(color(style.borderTopColor), outside), outside),
        ),
        3,
      );
    }
    if (el.matches('.mantine-Switch-track')) {
      check('switch-border', 'switch', contrast(blend(color(style.borderTopColor), outside), outside), 3);
    }
    if (el.matches('.mantine-Slider-track') && !el.closest('[data-disabled]')) {
      check(
        'slider-track',
        'unfilled',
        contrast(blend(color(getComputedStyle(el, '::before').backgroundColor), outside), outside),
        3,
      );
    }
    if (
      el.matches(
        ':focus-visible, .mantine-Switch-input:focus-visible + .mantine-Switch-track, .mantine-SegmentedControl-input:focus-visible + label',
      ) &&
      style.outlineStyle !== 'none' &&
      parseFloat(style.outlineWidth) > 0
    ) {
      check(
        'focus',
        el.getAttribute('aria-label') || text,
        contrast(blend(color(style.outlineColor), outside), outside),
        3,
      );
    }
  }
  return {
    checks,
    failures,
    overflow: document.documentElement.scrollWidth > innerWidth,
  };
}
