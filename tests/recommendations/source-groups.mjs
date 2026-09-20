import { readFile } from 'node:fs/promises';

const root = new URL('../../apps/companion/src/companion/', import.meta.url);
const groups = {
  service: [
    'domain/service-recommendations.ts',
    'domain/recommendation-runtime-context.ts',
    'domain/recommendation-blocked-diagnostics.ts',
    'domain/special-business/candidate-constraints.ts',
  ],
  workbench: [
    'ModWorkbench.tsx',
    'hooks/useOrderAutomation.ts',
    'hooks/useAutomationState.ts',
    'hooks/useAutomationControl.ts',
    'hooks/useAutomationSchedulers.ts',
    'domain/recommendation-input.ts',
    'domain/automation-diagnostics.ts',
    'domain/automation-lifecycle.ts',
    'domain/automation-target.ts',
  ],
  recommendationSettings: ['pages/settings/RecommendationSettingsPanel.tsx'],
  orderWorker: ['workers/order-recommendations.worker.ts', 'workers/order-recommendations-controller.ts'],
};

/** Existing architecture guards follow the implementation's explicit domain boundaries. */
export async function readSourceGroup(group) {
  return (await Promise.all(groups[group].map((path) => readFile(new URL(path, root), 'utf8')))).join('\n');
}
