import { readFile } from 'node:fs/promises';

// Audit the live ownership modules together after splitting the workbench.
// Keeping this list explicit also extends forbidden-path checks to every owner.
export const automationSourcePaths = [
  'apps/companion/src/companion/ModWorkbench.tsx',
  'apps/companion/src/companion/domain/recommendation-input.ts',
  'apps/companion/src/companion/domain/automation-constants.ts',
  'apps/companion/src/companion/domain/automation-target.ts',
  'apps/companion/src/companion/domain/automation-request-plan.ts',
  'apps/companion/src/companion/domain/automation-diagnostics.ts',
  'apps/companion/src/companion/domain/automation-lifecycle.ts',
  'apps/companion/src/companion/hooks/useAutomationState.ts',
  'apps/companion/src/companion/hooks/useAutomationControl.ts',
  'apps/companion/src/companion/hooks/useOrderAutomation.ts',
  'apps/companion/src/companion/hooks/useAutomationSchedulers.ts',
];

export async function readAutomationSources() {
  const root = new URL('../../', import.meta.url);
  return (await Promise.all(automationSourcePaths.map((path) => readFile(new URL(path, root), 'utf8')))).join('\n');
}
