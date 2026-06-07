export type TPlace =
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

export const ALL_PLACES: TPlace[] = [
  '妖怪兽道', '人间之里', '博丽神社', '红魔馆', '迷途竹林',
  '魔法森林', '妖怪之山', '旧地狱', '地灵殿', '命莲寺',
  '神灵庙', '太阳花田', '辉针城', '月之都', '魔界',
];

export type TDlc = 0 | 1 | 2 | 2.5 | 3 | 4 | 5 | 9;

export interface IRecipe {
  id: number;
  recipeId: number;
  name: string;
  description: string;
  ingredients: string[];
  // 料理自带标签（标签本体中性，不代表“正面”）
  positiveTags: string[];
  // 料理禁忌标签（命中会触发黑暗物质相关机制）
  negativeTags: string[];
  cooker: string;
  baseCookTime: number;
  dlc: TDlc;
  level: number;
  price: number;
  from: Record<string, unknown>;
}

export interface IIngredient {
  id: number;
  name: string;
  description: string;
  type: string;
  tags: string[];
  dlc: TDlc;
  level: number;
  price: number;
  from: Record<string, unknown>;
}

export interface IBeverage {
  id: number;
  name: string;
  description: string;
  tags: string[];
  dlc: TDlc;
  level: number;
  price: number;
  from: Record<string, unknown>;
}

export interface ICustomerNormal {
  id: number;
  name: string;
  description: string;
  dlc: TDlc;
  places: TPlace[];
  // 顾客喜好标签（偏好关系，不是标签本体“正面”）
  positiveTags: string[];
  beverageTags: string[];
}

export interface ICustomerRare {
  id: number;
  name: string;
  description: string | string[];
  dlc: TDlc;
  places: TPlace[];
  price: number[];
  enduranceLimit: number;
  // 顾客喜好标签（偏好关系）
  positiveTags: string[];
  // 顾客厌恶标签（偏好关系）
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

export type TRating = 'ExGood' | 'Good' | 'Normal' | 'Bad' | 'ExBad';

export interface ICustomerScore {
  name: string;
  score: number;
}

export interface INormalRecipeResult {
  recipe: IRecipe;
  customerScores: ICustomerScore[];
  totalCoverage: number;
  profit: number;
  matchedTags: string[];
  ingredientCost: number;
}

export interface INormalBeverageResult {
  beverage: IBeverage;
  customerScores: ICustomerScore[];
  totalCoverage: number;
  matchedTags: string[];
}

export interface IRareRecipeResult {
  recipe: IRecipe;
  extraIngredients: IIngredient[];
  // 记录每个加料被选中的用途标签（例如 { 12: ['甜', '果味'] }）
  extraIngredientReasonTags: Record<number, string[]>;
  isEasterPriority: boolean;
  isEasterPinned: boolean;
  isEasterRecipeHighlight: boolean;
  easterHighlightExtraIngredientIds: number[];
  easterReason: string | null;
  easterScoreFloor: number | null;
  allTags: string[];
  cancelledTags: string[];
  foodScore: number;
  meetsRequiredFood: boolean;
  rating: TRating;
  baseCost: number;
  extraCost: number;
}

export interface IRareBeverageResult {
  beverage: IBeverage;
  bevScore: number;
  meetsRequiredBev: boolean;
  matchedTags: string[];
}
