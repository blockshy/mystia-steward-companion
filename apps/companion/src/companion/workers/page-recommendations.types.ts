import type { CompanionPreferences } from '@/companion/preferences';
import type {
  CustomRecipeData,
  FavoriteData,
  RecommendationStateSnapshot,
} from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { PlaceName, RareCustomerCatalogItem } from '@/lib/catalog-types';
import type {
  NormalBeverageRecommendation,
  NormalRecipeRecommendation,
  RareBeverageRecommendation,
  RareRecipeRecommendation,
} from '@/recommendation-engine';

export type PageRecommendationPayload =
  | NormalPageRecommendationPayload
  | RarePageRecommendationPayload;

export interface NormalPageRecommendationPayload {
  kind: 'normal';
  runtime: RecommendationStateSnapshot;
  selectedPlace: PlaceName;
  data: RecommendationDataSet;
}

export interface RarePageRecommendationPayload {
  kind: 'rare';
  runtime: RecommendationStateSnapshot;
  selectedCustomer: RareCustomerCatalogItem;
  foodTag: string;
  beverageTag: string;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  preferences: CompanionPreferences;
  data: RecommendationDataSet;
}

export type PageRecommendationResult =
  | NormalPageRecommendationResult
  | RarePageRecommendationResult;

export interface NormalPageRecommendationResult {
  kind: 'normal';
  recipes: NormalRecipeRecommendation[];
  beverages: NormalBeverageRecommendation[];
}

export interface RarePageRecommendationResult {
  kind: 'rare';
  recipes: RareRecipeRecommendation[];
  beverages: RareBeverageRecommendation[];
}

export interface PageRecommendationWorkerRequest {
  requestId: number;
  payload: PageRecommendationPayload;
}

export type PageRecommendationWorkerResponse =
  | {
    requestId: number;
    ok: true;
    result: PageRecommendationResult;
  }
  | {
    requestId: number;
    ok: false;
    error: string;
  };
