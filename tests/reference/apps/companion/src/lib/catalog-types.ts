export type PlaceName =
  | '妖怪兽道'
  | '人间之里'
  | '博丽神社'
  | '红魔馆'
  | '迷途竹林'
  | '魔法森林'
  | '妖怪之山'
  | '旧地狱'
  | '地灵殿'
  | '命莲寺'
  | '神灵庙'
  | '太阳花田'
  | '辉针城'
  | '月之都'
  | '魔界';

export const ALL_PLACES: PlaceName[] = [
  '妖怪兽道', '人间之里', '博丽神社', '红魔馆', '迷途竹林',
  '魔法森林', '妖怪之山', '旧地狱', '地灵殿', '命莲寺',
  '神灵庙', '太阳花田', '辉针城', '月之都', '魔界',
];

export type DlcCode = 0 | 1 | 2 | 2.5 | 3 | 4 | 5 | 9;

export interface RecipeCatalogItem {
  id: number;
  recipeId: number;
  name: string;
  description: string;
  // 基础配方食材是数量敏感序列，重复项表示同一材料需要多份。
  ingredients: string[];
  positiveTags: string[];
  negativeTags: string[];
  cooker: string;
  baseCookTime: number;
  dlc: DlcCode;
  level: number;
  price: number;
  from: Record<string, unknown>;
}

export interface IngredientCatalogItem {
  id: number;
  name: string;
  description: string;
  type: string;
  tags: string[];
  dlc: DlcCode;
  level: number;
  price: number;
  from: Record<string, unknown>;
}

export interface BeverageCatalogItem {
  id: number;
  name: string;
  description: string;
  tags: string[];
  dlc: DlcCode;
  level: number;
  price: number;
  from: Record<string, unknown>;
}

export interface NormalCustomerCatalogItem {
  id: number;
  name: string;
  description: string;
  dlc: DlcCode;
  places: PlaceName[];
  positiveTags: string[];
  beverageTags: string[];
}

export interface RareCustomerCatalogItem {
  id: number;
  name: string;
  description: string | string[];
  dlc: DlcCode;
  places: PlaceName[];
  price: number[];
  enduranceLimit: number;
  positiveTags: string[];
  negativeTags: string[];
  beverageTags: string[];
  positiveTagMapping?: Record<string, string>;
  beverageTagMapping?: Record<string, string>;
  collection: boolean;
  evaluation: Record<string, string>;
  spellCards: {
    positive: Array<{ name: string; description: string }>;
    negative: Array<{ name: string; description: string }>;
  };
}
