import { MantineProvider, createTheme } from '@mantine/core';
import type { CSSVariablesResolver } from '@mantine/core';
import type { ReactNode } from 'react';

import { useThemeMode } from '@/lib/theme';

const companionTheme = createTheme({
  fontFamily: "'Geist Variable', sans-serif",
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
