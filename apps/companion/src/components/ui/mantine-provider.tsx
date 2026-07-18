import { MantineProvider, createTheme } from '@mantine/core';
import type { CSSVariablesResolver } from '@mantine/core';
import type { ReactNode } from 'react';

import { useThemeMode } from '@/lib/theme';

const companionTheme = createTheme({
  fontFamily: "'Geist Variable', sans-serif",
  fontSizes: {
    xs: 'calc(0.75rem * var(--companion-font-scale))',
    sm: 'calc(0.875rem * var(--companion-font-scale))',
    md: 'calc(1rem * var(--companion-font-scale))',
    lg: 'calc(1.125rem * var(--companion-font-scale))',
    xl: 'calc(1.25rem * var(--companion-font-scale))',
  },
  headings: {
    sizes: {
      h1: { fontSize: 'calc(2.125rem * var(--companion-font-scale))' },
      h2: { fontSize: 'calc(1.625rem * var(--companion-font-scale))' },
      h3: { fontSize: 'calc(1.375rem * var(--companion-font-scale))' },
      h4: { fontSize: 'calc(1.125rem * var(--companion-font-scale))' },
      h5: { fontSize: 'calc(1rem * var(--companion-font-scale))' },
      h6: { fontSize: 'calc(0.875rem * var(--companion-font-scale))' },
    },
  },
  primaryColor: 'steward',
  defaultRadius: 0,
  primaryShade: { light: 6, dark: 4 },
  colors: {
    steward: [
      '#fff0c8',
      '#f6ddb0',
      '#e9c28b',
      '#d99a5a',
      '#f08a35',
      '#bd6430',
      '#a93b25',
      '#87301e',
      '#622516',
      '#3f1813',
    ],
  },
  cursorType: 'pointer',
});

const companionCssVariablesResolver: CSSVariablesResolver = () => ({
  variables: {},
  light: {
    '--mantine-color-body': 'transparent',
  },
  dark: {
    '--mantine-color-body': 'transparent',
  },
});

function CompanionMantineProvider({ children }: { children: ReactNode }) {
  const { resolvedTheme } = useThemeMode();

  return (
    <MantineProvider
      theme={companionTheme}
      cssVariablesResolver={companionCssVariablesResolver}
      forceColorScheme={resolvedTheme}
      defaultColorScheme="dark"
    >
      {children}
    </MantineProvider>
  );
}

export { CompanionMantineProvider };
