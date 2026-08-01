import type { RareCustomerCatalogItem } from '@/lib/catalog-types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

export function resolveExactSpecialBusinessCustomer(
  data: RecommendationDataSet,
  canonicalGuestId: number,
): RareCustomerCatalogItem | null {
  const profile = data.rareCustomerProfiles.find(
    (candidate) => candidate.id === canonicalGuestId,
  );
  if (!profile) return null;

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
