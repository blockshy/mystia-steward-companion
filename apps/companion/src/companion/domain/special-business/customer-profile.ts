import type { RareCustomerCatalogItem } from '@/lib/catalog-types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

export function resolveExactSpecialBusinessCustomer(
  data: RecommendationDataSet,
  canonicalGuestId: number,
): RareCustomerCatalogItem | null {
  if (!Number.isSafeInteger(canonicalGuestId) || canonicalGuestId < 0) return null;
  const profiles = data.rareCustomerProfiles.filter(
    (candidate) => candidate.id === canonicalGuestId,
  );
  if (profiles.length !== 1) return null;
  const profile = profiles[0];

  return {
    id: profile.id,
    name: profile.name,
    description: '',
    dlc: 0,
    places: [],
    price: [0, 0],
    enduranceLimit: 1,
    positiveTags: profile.positiveTags,
    negativeTags: profile.negativeTags,
    beverageTags: profile.beverageTags,
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
}
