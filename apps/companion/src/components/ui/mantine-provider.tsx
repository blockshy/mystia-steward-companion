import { MantineProvider, createTheme } from '@mantine/core';
import type { ReactNode } from 'react';

import { useThemeMode } from '@/lib/theme';

const companionTheme = createTheme({
  fontFamily: "'Geist Variable', sans-serif",
  primaryColor: 'steward',
  defaultRadius: 'md',
  colors: {
    steward: [
      '#fff7ed',
      '#fee8d2',
      '#facda7',
      '#f2ae78',
      '#e9944e',
      '#e18232',
      '#d37527',
      '#b8602a',
      '#934b25',
      '#773f22',
    ],
  },
  cursorType: 'pointer',
});

function CompanionMantineProvider({ children }: { children: ReactNode }) {
  const { resolvedTheme } = useThemeMode();

  return (
    <MantineProvider
      theme={companionTheme}
      forceColorScheme={resolvedTheme}
      defaultColorScheme="dark"
    >
      {children}
    </MantineProvider>
  );
}

export { CompanionMantineProvider };
