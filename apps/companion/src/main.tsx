import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import '@mantine/core/styles.css'
import '@/index.css'
import App from '@/App.tsx'
import { CompanionMantineProvider } from '@/components/ui/mantine-provider'
import { applyThemeMode, readThemeMode } from '@/lib/theme'

applyThemeMode(readThemeMode())

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <CompanionMantineProvider>
      <App />
    </CompanionMantineProvider>
  </StrictMode>,
)
