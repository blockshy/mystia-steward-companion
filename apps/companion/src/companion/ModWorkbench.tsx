import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode, SetStateAction } from 'react';
import { FolderOpen, Power, RefreshCw } from 'lucide-react';
import { CustomerScoreBadges } from '@/components/ScoreBadge';
import { RegionSelector } from '@/components/RegionSelector';
import { TagBadge } from '@/components/TagBadge';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import {
  computeNormalBeverageResults,
  computeNormalRecipeResults,
  getNormalCustomersByPlace,
} from '@/lib/normal-recommend';
import {
  getAllRareCustomers,
  getRareCustomersByPlace,
  rankBeveragesForRare,
  rankRecipesForRare,
} from '@/lib/rare-recommend';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import type {
  IBeverage,
  ICustomerRare,
  IIngredient,
  INormalBeverageResult,
  INormalRecipeResult,
  IRareBeverageResult,
  IRareRecipeResult,
  TPlace,
  TRating,
} from '@/lib/types';
import { ALL_PLACES } from '@/lib/types';
import allIngredients from '@/data/ingredients.json';
import allBeverages from '@/data/beverages.json';

const DEFAULT_ENDPOINT = 'http://127.0.0.1:32145';
const STORAGE_PREFIX = 'mystia-steward-companion';
const LEGACY_STORAGE_PREFIX = 'mystia-steward';
const ENDPOINT_STORAGE_KEY = `${STORAGE_PREFIX}-mod-api-endpoint`;
const TOKEN_STORAGE_KEY = `${STORAGE_PREFIX}-mod-api-token`;
const TAB_STORAGE_KEY = `${STORAGE_PREFIX}-mod-tab`;
const LEGACY_ENDPOINT_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-api-endpoint`;
const LEGACY_TOKEN_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-api-token`;
const LEGACY_TAB_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-tab`;
const MAX_RECOMMENDATION_ROWS = 8;
const MAX_LOG_LINES_IN_VIEW = 400;
const NON_ORDERABLE_RARE_FOOD_TAGS = new Set(['流行喜爱', '流行厌恶']);
const INGREDIENTS = allIngredients as IIngredient[];
const INGREDIENT_ID_BY_NAME = new Map(INGREDIENTS.map((ingredient) => [ingredient.name, ingredient.id]));
const INGREDIENT_NAME_BY_ID = new Map(INGREDIENTS.map((ingredient) => [ingredient.id, ingredient.name]));
const BEVERAGES = allBeverages as IBeverage[];
const BEVERAGE_NAME_BY_ID = new Map(BEVERAGES.map((beverage) => [beverage.id, beverage.name]));
const RIGHT_STICK_GAMEPAD_BUTTON_INDEX = 11;

type ModTab = 'overview' | 'normal' | 'rare' | 'service' | 'inventory' | 'logs';

const RATING_LABELS: Record<TRating, string> = {
  ExGood: '完美',
  Good: '满意',
  Normal: '普通',
  Bad: '不满',
  ExBad: '极差',
};

interface RecommendationStateSnapshot {
  availableRecipeIds: number[];
  availableBeverageIds: number[];
  availableIngredientIds: number[];
  ownedIngredientQty: Record<string, number>;
  ownedBeverageQty: Record<string, number>;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
}

interface NightBusinessGuest {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  source: string;
}

interface NightBusinessOrder {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  foodTagId: number;
  foodTag: string;
  beverageTagId: number;
  beverageTag: string;
  source: string;
  firstSeenAtUtc?: string | null;
  lastSeenAtUtc?: string | null;
}

interface NightBusinessContext {
  place: string | null;
  placeLabel: string | null;
  activeRareGuests: NightBusinessGuest[];
  orders: NightBusinessOrder[];
  source: string;
  error: string | null;
}

interface LocalApiSnapshot {
  pluginVersion: string;
  capturedAtUtc: string;
  activeSceneName: string;
  runtimeLoaded: boolean;
  status: string;
  runtimeSource: string;
  dataDirectory: string;
  recommendationState: RecommendationStateSnapshot | null;
  nightBusiness: NightBusinessContext | null;
}

interface RuntimeSets {
  recipeIds: Set<number>;
  beverageIds: Set<number>;
  ingredientIds: Set<number>;
  unavailableIngredientIds: Set<number>;
  ownedIngredientQty: Record<number, number>;
  ownedBeverageQty: Record<number, number>;
}

interface CachedRecommendation {
  customer: ICustomerRare;
  recipes: IRareRecipeResult[];
  beverages: IRareBeverageResult[];
}

interface OrderRecommendation extends CachedRecommendation {
  order: NightBusinessOrder;
}

interface RecommendationIssue {
  order: NightBusinessOrder;
  message: string;
}

interface LocalApiLogs {
  capturedAtUtc: string;
  path: string;
  exists: boolean;
  enabled: boolean;
  maxLines?: number;
  maxBytes?: number;
  lines: string[];
  error: string | null;
}

interface LocalApiLogSettings {
  logAccessEnabled: boolean;
  logOutputPath: string;
  logOutputDirectory: string;
  maxLogLines?: number;
  maxLogBytes?: number;
  nightBusinessDiagnosticsEnabled: boolean;
  nightBusinessDiagnosticsPath: string;
  nightBusinessDiagnosticsDirectory: string;
}

interface LocalApiFolderResponse {
  ok: boolean;
  directory: string;
  error: string | null;
}

interface InventoryEditResponse {
  ok: boolean;
  type: 'ingredient' | 'beverage';
  id: number;
  requestedQuantity: number;
  previousQuantity: number;
  quantity: number;
  changed: boolean;
  error: string | null;
}

export function ModWorkbench() {
  const [endpoint, setEndpoint] = useState(() =>
    readMigratedStorage(ENDPOINT_STORAGE_KEY, LEGACY_ENDPOINT_STORAGE_KEY, DEFAULT_ENDPOINT),
  );
  const [apiToken, setApiToken] = useState(() =>
    readMigratedStorage(TOKEN_STORAGE_KEY, LEGACY_TOKEN_STORAGE_KEY, ''),
  );
  const [tab, setTab] = useState<ModTab>(() => readStoredTab());
  const [serviceFocusMode, setServiceFocusMode] = useState(false);
  const [serviceFocusCompact, setServiceFocusCompact] = useState(false);
  const [snapshot, setSnapshot] = useState<LocalApiSnapshot | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [lastConnectedAt, setLastConnectedAt] = useState<Date | null>(null);
  const [manualPlace, setManualPlace] = useState<TPlace | null>(null);
  const [rareCustomerId, setRareCustomerId] = useState<number | null>(null);
  const [requiredFoodTag, setRequiredFoodTag] = useState('');
  const [requiredBeverageTag, setRequiredBeverageTag] = useState('');
  const recommendationCacheRef = useRef(new Map<string, CachedRecommendation>());

  const normalizedEndpoint = useMemo(() => normalizeEndpoint(endpoint), [endpoint]);
  const runtime = snapshot?.recommendationState ?? null;
  const night = snapshot?.nightBusiness ?? null;
  const detectedPlace = normalizePlace(night?.place);
  const selectedPlace = manualPlace ?? detectedPlace;
  const rareCustomersById = useMemo(() => new Map(getAllRareCustomers().map((customer) => [customer.id, customer])), []);

  const runtimeSets = useMemo(() => buildRuntimeSets(runtime), [runtime]);
  const orderRecommendations = useMemo(
    () => buildOrderRecommendations(night?.orders ?? [], runtime, rareCustomersById, recommendationCacheRef.current),
    [night?.orders, runtime, rareCustomersById],
  );
  const snapshotRefreshIntervalMs = tab === 'service' || serviceFocusMode ? 750 : 2000;

  const refresh = useCallback(async () => {
    setLoading(true);
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);

    try {
      const data = await readSnapshot(normalizedEndpoint, apiToken, abortController.signal);
      setSnapshot(data);
      setError('');
      setLastConnectedAt(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setLoading(false);
    }
  }, [apiToken, normalizedEndpoint]);

  useEffect(() => {
    localStorage.setItem(ENDPOINT_STORAGE_KEY, normalizedEndpoint);
  }, [normalizedEndpoint]);

  useEffect(() => {
    if (apiToken) localStorage.setItem(TOKEN_STORAGE_KEY, apiToken);
  }, [apiToken]);

  useEffect(() => {
    localStorage.setItem(TAB_STORAGE_KEY, tab);
  }, [tab]);

  useEffect(() => {
    if (!isTauriRuntime()) return;

    let disposed = false;
    import('@tauri-apps/api/core')
      .then(async ({ invoke }) => {
        const [launchEndpoint, launchToken] = await Promise.all([
          invoke<string | null>('launch_api_endpoint'),
          invoke<string | null>('launch_api_token'),
        ]);
        return { launchEndpoint, launchToken };
      })
      .then(({ launchEndpoint, launchToken }) => {
        if (!disposed && launchEndpoint) setEndpoint(launchEndpoint);
        if (!disposed && launchToken) setApiToken(launchToken);
      })
      .catch(() => {
        // Browser mode does not expose launch arguments.
      });

    return () => {
      disposed = true;
    };
  }, []);

  useEffect(() => {
    if (!isTauriRuntime()) return;

    let disposed = false;
    let animationFrame = 0;
    let rightStickPressed = false;
    let gamepadToggleArmed = false;
    let lastToggleAt = 0;

    const requestToggle = () => {
      const now = Date.now();
      if (now - lastToggleAt < 1200) return;
      lastToggleAt = now;
      void toggleCompanionFocus();
    };

    const isRightStickPressed = () => {
      const gamepads = navigator.getGamepads?.() ?? [];
      return Array.from(gamepads).some(
        (gamepad) => gamepad?.connected && gamepad.buttons[RIGHT_STICK_GAMEPAD_BUTTON_INDEX]?.pressed,
      );
    };

    const resetGamepadLatch = () => {
      rightStickPressed = isRightStickPressed();
      gamepadToggleArmed = !rightStickPressed;
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'F8' || event.repeat) return;
      event.preventDefault();
      requestToggle();
    };

    const watchGamepads = () => {
      if (disposed) return;

      const nextRightStickPressed = isRightStickPressed();

      if (!nextRightStickPressed) {
        gamepadToggleArmed = true;
      } else if (gamepadToggleArmed && !rightStickPressed && document.hasFocus()) {
        requestToggle();
        gamepadToggleArmed = false;
      }

      rightStickPressed = nextRightStickPressed;
      animationFrame = window.requestAnimationFrame(watchGamepads);
    };

    resetGamepadLatch();
    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('focus', resetGamepadLatch);
    document.addEventListener('visibilitychange', resetGamepadLatch);
    animationFrame = window.requestAnimationFrame(watchGamepads);

    return () => {
      disposed = true;
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('focus', resetGamepadLatch);
      document.removeEventListener('visibilitychange', resetGamepadLatch);
      if (animationFrame) window.cancelAnimationFrame(animationFrame);
    };
  }, []);

  useEffect(() => {
    refresh();
    const timer = window.setInterval(refresh, snapshotRefreshIntervalMs);
    return () => window.clearInterval(timer);
  }, [refresh, snapshotRefreshIntervalMs]);

  if (serviceFocusMode) {
    return (
      <ServiceFocusPage
        recommendations={orderRecommendations.recommendations}
        recommendationIssues={orderRecommendations.recommendationIssues}
        runtimeSets={runtimeSets}
        compact={serviceFocusCompact}
        onCompactChange={setServiceFocusCompact}
        onExit={() => setServiceFocusMode(false)}
      />
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-foreground">Mod 工作台</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {snapshot ? `mystia-steward-companion ${snapshot.pluginVersion}` : '等待本地 API 响应'}
          </p>
        </div>
        <div className="flex w-full max-w-xl items-center gap-2">
          <Input
            value={endpoint}
            onChange={(event) => setEndpoint(event.target.value)}
            spellCheck={false}
            className="font-mono text-xs"
          />
          <Button size="sm" onClick={refresh} disabled={loading}>
            <RefreshCw className={loading ? 'size-4 animate-spin' : 'size-4'} />
            刷新
          </Button>
        </div>
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
        <StatusCard
          label="连接状态"
          value={error ? '未连接' : snapshot ? '已连接' : '连接中'}
          detail={error || (lastConnectedAt ? `最近响应 ${formatTime(lastConnectedAt)}` : normalizedEndpoint)}
          tone={error ? 'bad' : snapshot ? 'good' : 'neutral'}
        />
        <StatusCard
          label="游戏运行态"
          value={snapshot?.runtimeLoaded ? '已加载' : '未加载'}
          detail={snapshot?.activeSceneName || snapshot?.status || '暂无快照'}
          tone={snapshot?.runtimeLoaded ? 'good' : 'neutral'}
        />
        <StatusCard
          label="经营数据"
          value={`${night?.activeRareGuests.length ?? 0} 稀客 / ${night?.orders.length ?? 0} 点单`}
          detail={night?.place || night?.placeLabel || '无经营场景'}
          tone={(night?.orders.length ?? 0) > 0 ? 'good' : 'neutral'}
        />
      </div>

      <Tabs value={tab} onValueChange={(value) => setTab(value as ModTab)} className="space-y-4">
        <TabsList className="h-9 !w-full max-w-full justify-stretch overflow-x-auto overflow-y-hidden [scrollbar-width:none] [&::-webkit-scrollbar]:hidden">
          <TabsTrigger value="overview" className="min-w-0 flex-1">
            概览
          </TabsTrigger>
          <TabsTrigger value="normal" className="min-w-0 flex-1">
            普客
          </TabsTrigger>
          <TabsTrigger value="rare" className="min-w-0 flex-1">
            稀客
          </TabsTrigger>
          <TabsTrigger value="service" className="min-w-0 flex-1">
            经营中
          </TabsTrigger>
          <TabsTrigger value="inventory" className="min-w-0 flex-1">
            修改
          </TabsTrigger>
          <TabsTrigger value="logs" className="min-w-0 flex-1">
            日志
          </TabsTrigger>
        </TabsList>

        <TabsContent value="overview">
          <ModOverviewPanel
            endpoint={normalizedEndpoint}
            snapshot={snapshot}
            runtime={runtime}
            night={night}
            error={error}
            lastConnectedAt={lastConnectedAt}
          />
        </TabsContent>

        <TabsContent value="normal">
          <ModNormalPanel
            runtime={runtime}
            runtimeSets={runtimeSets}
            selectedPlace={selectedPlace}
            detectedPlace={detectedPlace}
            onPlaceChange={setManualPlace}
            onFollowDetectedPlace={() => setManualPlace(null)}
          />
        </TabsContent>

        <TabsContent value="rare">
          <ModRarePanel
            runtime={runtime}
            runtimeSets={runtimeSets}
            selectedPlace={selectedPlace}
            detectedPlace={detectedPlace}
            rareCustomerId={rareCustomerId}
            requiredFoodTag={requiredFoodTag}
            requiredBeverageTag={requiredBeverageTag}
            onPlaceChange={(place) => {
              setManualPlace(place);
              setRareCustomerId(null);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onFollowDetectedPlace={() => {
              setManualPlace(null);
              setRareCustomerId(null);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onRareCustomerChange={(customerId) => {
              setRareCustomerId(customerId);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onFoodTagChange={setRequiredFoodTag}
            onBeverageTagChange={setRequiredBeverageTag}
          />
        </TabsContent>

        <TabsContent value="service">
          <ModServicePanel
            runtime={runtime}
            night={night}
            detectedPlace={detectedPlace}
            recommendations={orderRecommendations.recommendations}
            recommendationIssues={orderRecommendations.recommendationIssues}
            runtimeSets={runtimeSets}
            onEnterFocusMode={() => setServiceFocusMode(true)}
          />
        </TabsContent>

        <TabsContent value="inventory">
          <ModInventoryPanel
            endpoint={normalizedEndpoint}
            apiToken={apiToken}
            runtimeSets={runtimeSets}
            runtimeLoaded={snapshot?.runtimeLoaded ?? false}
            onRefresh={refresh}
          />
        </TabsContent>

        <TabsContent value="logs">
          <ModLogsPanel endpoint={normalizedEndpoint} apiToken={apiToken} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function ModOverviewPanel({
  endpoint,
  snapshot,
  runtime,
  night,
  error,
  lastConnectedAt,
}: {
  endpoint: string;
  snapshot: LocalApiSnapshot | null;
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  error: string;
  lastConnectedAt: Date | null;
}) {
  const ownedIngredientEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedIngredientQty ?? {}, INGREDIENT_NAME_BY_ID),
    [runtime?.ownedIngredientQty],
  );
  const ownedBeverageEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedBeverageQty ?? {}, BEVERAGE_NAME_BY_ID),
    [runtime?.ownedBeverageQty],
  );

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="grid gap-3 p-4 text-sm md:grid-cols-2">
          <InfoLine label="数据来源" value="游戏实时 API，不读取 .memory 存档" />
          <InfoLine label="API 地址" value={endpoint} mono />
          <InfoLine label="连接状态" value={error ? `未连接: ${error}` : snapshot ? '已连接' : '连接中'} />
          <InfoLine label="最近响应" value={lastConnectedAt ? formatTime(lastConnectedAt) : '暂无'} />
          <InfoLine label="场景" value={snapshot?.activeSceneName || '未知'} />
          <InfoLine label="运行时状态" value={snapshot?.status || '暂无快照'} />
          <InfoLine label="运行时来源" value={snapshot?.runtimeSource || '未知'} />
          <InfoLine label="数据目录" value={snapshot?.dataDirectory || '未知'} mono />
        </CardContent>
      </Card>

      <Card>
        <CardContent className="grid gap-3 p-4 text-sm md:grid-cols-4">
          <Metric label="可用料理" value={runtime?.availableRecipeIds.length ?? 0} />
          <Metric label="可用酒水" value={runtime?.availableBeverageIds.length ?? 0} />
          <Metric label="可用食材" value={runtime?.availableIngredientIds.length ?? 0} />
          <Metric label="明星店" value={runtime?.famousShopEnabled ? '开启' : '关闭'} />
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <ListPanel title="快捷键">
          <div className="grid gap-2 text-sm">
            <InfoLine label="F8" value="在游戏与独立窗口之间切换；若启用旧游戏内面板，则打开或关闭游戏内面板" />
            <InfoLine label="RS Click" value="手柄默认在游戏与独立窗口之间切换" />
            <InfoLine label="F9" value="刷新游戏运行时数据检测" />
            <InfoLine label="窗口关闭" value="关闭按钮会隐藏到托盘；托盘菜单可重新显示或退出" />
          </div>
        </ListPanel>

        <ListPanel title="实时标签">
          <InfoLine label="流行喜爱" value={runtime?.popularFoodTag || '无'} />
          <InfoLine label="流行厌恶" value={runtime?.popularHateFoodTag || '无'} />
          <InfoLine label="当前经营场景" value={night?.place || night?.placeLabel || '无经营场景'} />
          <InfoLine label="经营扫描" value={night?.source || '暂无'} />
        </ListPanel>

        <ListPanel title="低库存概览">
          <div className="grid gap-4 md:grid-cols-2">
            <LowStockColumn title="材料" entries={ownedIngredientEntries} />
            <LowStockColumn title="酒水" entries={ownedBeverageEntries} />
          </div>
        </ListPanel>
      </div>
    </div>
  );
}

function ModNormalPanel({
  runtime,
  runtimeSets,
  selectedPlace,
  detectedPlace,
  onPlaceChange,
  onFollowDetectedPlace,
}: {
  runtime: RecommendationStateSnapshot | null;
  runtimeSets: RuntimeSets | null;
  selectedPlace: TPlace | null;
  detectedPlace: TPlace | null;
  onPlaceChange: (place: TPlace) => void;
  onFollowDetectedPlace: () => void;
}) {
  const recipes = useMemo(() => {
    if (!runtime || !runtimeSets || !selectedPlace) return [];
    return computeNormalRecipeResults(
      selectedPlace,
      runtimeSets.recipeIds,
      runtimeSets.unavailableIngredientIds,
      runtime.popularFoodTag,
      runtime.popularHateFoodTag,
      runtime.famousShopEnabled,
    )
      .sort(compareNormalRecipesForMod)
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [runtime, runtimeSets, selectedPlace]);

  const beverages = useMemo(() => {
    if (!runtimeSets || !selectedPlace) return [];
    return computeNormalBeverageResults(selectedPlace, runtimeSets.beverageIds)
      .sort(compareNormalBeveragesForMod)
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [runtimeSets, selectedPlace]);

  const customers = useMemo(
    () => (selectedPlace ? getNormalCustomersByPlace(selectedPlace) : []),
    [selectedPlace],
  );

  if (!runtime || !runtimeSets) return <RuntimeUnavailable />;

  return (
    <div className="space-y-4">
      <PlaceToolbar
        selectedPlace={selectedPlace}
        detectedPlace={detectedPlace}
        onPlaceChange={onPlaceChange}
        onFollowDetectedPlace={onFollowDetectedPlace}
      />

      {!selectedPlace && <EmptyState text="请选择地区后查看普客推荐" />}

      {selectedPlace && (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <ListPanel title={`料理推荐 (${recipes.length})`}>
            {recipes.length === 0 && <EmptyRow text="暂无可推荐料理" />}
            <div className="space-y-2">
              {recipes.map((recipe, index) => (
                <NormalRecipeRow
                  key={recipe.recipe.id}
                  recipe={recipe}
                  index={index}
                  ownedIngredientQty={runtimeSets.ownedIngredientQty}
                />
              ))}
            </div>
          </ListPanel>

          <ListPanel title={`酒水推荐 (${beverages.length})`}>
            {beverages.length === 0 && <EmptyRow text="暂无可推荐酒水" />}
            <div className="space-y-2">
              {beverages.map((beverage, index) => (
                <NormalBeverageRow
                  key={beverage.beverage.id}
                  beverage={beverage}
                  index={index}
                  ownedBeverageQty={runtimeSets.ownedBeverageQty}
                />
              ))}
            </div>
          </ListPanel>
        </div>
      )}

      {selectedPlace && (
        <ListPanel title={`地区普客 (${customers.length})`}>
          <div className="grid grid-cols-1 gap-2 md:grid-cols-2 xl:grid-cols-3">
            {customers.map((customer) => (
              <div key={customer.id} className="rounded-md border border-border/80 p-2 text-sm">
                <div className="font-medium">{customer.name}</div>
                <div className="mt-1 flex flex-wrap gap-1">
                  {customer.positiveTags.map((tag) => <TagBadge key={tag} tag={tag} variant="preferred" />)}
                  {customer.beverageTags.map((tag) => <TagBadge key={tag} tag={tag} variant="default" />)}
                </div>
              </div>
            ))}
          </div>
        </ListPanel>
      )}
    </div>
  );
}

function ModRarePanel({
  runtime,
  runtimeSets,
  selectedPlace,
  detectedPlace,
  rareCustomerId,
  requiredFoodTag,
  requiredBeverageTag,
  onPlaceChange,
  onFollowDetectedPlace,
  onRareCustomerChange,
  onFoodTagChange,
  onBeverageTagChange,
}: {
  runtime: RecommendationStateSnapshot | null;
  runtimeSets: RuntimeSets | null;
  selectedPlace: TPlace | null;
  detectedPlace: TPlace | null;
  rareCustomerId: number | null;
  requiredFoodTag: string;
  requiredBeverageTag: string;
  onPlaceChange: (place: TPlace) => void;
  onFollowDetectedPlace: () => void;
  onRareCustomerChange: (customerId: number | null) => void;
  onFoodTagChange: (tag: string) => void;
  onBeverageTagChange: (tag: string) => void;
}) {
  const customers = useMemo(
    () => (selectedPlace ? getRareCustomersByPlace(selectedPlace) : []),
    [selectedPlace],
  );
  const selectedCustomer = customers.find((customer) => customer.id === rareCustomerId) ?? customers[0] ?? null;
  const foodTag = requiredFoodTag || selectedCustomer?.positiveTags.find(isOrderableRareFoodTag) || '';
  const beverageTag = requiredBeverageTag || selectedCustomer?.beverageTags[0] || '';

  useEffect(() => {
    if (!selectedCustomer) {
      if (rareCustomerId !== null) onRareCustomerChange(null);
      return;
    }
    if (rareCustomerId !== selectedCustomer.id) onRareCustomerChange(selectedCustomer.id);
    if (!requiredFoodTag && foodTag) onFoodTagChange(foodTag);
    if (!requiredBeverageTag && beverageTag) onBeverageTagChange(beverageTag);
  }, [
    beverageTag,
    foodTag,
    onBeverageTagChange,
    onFoodTagChange,
    onRareCustomerChange,
    rareCustomerId,
    requiredBeverageTag,
    requiredFoodTag,
    selectedCustomer,
  ]);

  const recipes = useMemo(() => {
    if (!runtime || !runtimeSets || !selectedCustomer || !foodTag || !beverageTag) return [];
    return rankRecipesForRare(
      selectedCustomer,
      foodTag,
      beverageTag,
      runtimeSets.recipeIds,
      runtimeSets.ingredientIds,
      new Set<number>(),
      runtime.popularFoodTag,
      runtime.popularHateFoodTag,
      4,
      runtimeSets.ownedIngredientQty,
      runtime.famousShopEnabled,
    )
      .sort(compareRareRecipesForService)
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [beverageTag, foodTag, runtime, runtimeSets, selectedCustomer]);

  const beverages = useMemo(() => {
    if (!runtimeSets || !selectedCustomer || !beverageTag) return [];
    return rankBeveragesForRare(selectedCustomer, beverageTag, runtimeSets.beverageIds)
      .sort(compareRareBeveragesForService)
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [beverageTag, runtimeSets, selectedCustomer]);

  if (!runtime || !runtimeSets) return <RuntimeUnavailable />;

  return (
    <div className="space-y-4">
      <PlaceToolbar
        selectedPlace={selectedPlace}
        detectedPlace={detectedPlace}
        onPlaceChange={onPlaceChange}
        onFollowDetectedPlace={onFollowDetectedPlace}
      />

      {!selectedPlace && <EmptyState text="请选择地区后查看稀客推荐" />}

      {selectedPlace && customers.length === 0 && <EmptyState text="该地区没有稀客" />}

      {selectedPlace && selectedCustomer && (
        <>
          <Card>
            <CardContent className="grid gap-3 p-4 text-sm lg:grid-cols-3">
              <div>
                <div className="mb-1 text-xs text-muted-foreground">稀客</div>
                <Select value={String(selectedCustomer.id)} onValueChange={(value) => onRareCustomerChange(Number(value))}>
                  <SelectTrigger className="w-full">
                    <SelectValue>{selectedCustomer.name}</SelectValue>
                  </SelectTrigger>
                  <SelectContent>
                    {customers.map((customer) => (
                      <SelectItem key={customer.id} value={String(customer.id)}>{customer.name}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div>
                <div className="mb-1 text-xs text-muted-foreground">点单料理 Tag</div>
                <Select value={foodTag} onValueChange={(value) => onFoodTagChange(value ?? '')}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {selectedCustomer.positiveTags.filter(isOrderableRareFoodTag).map((tag) => (
                      <SelectItem key={tag} value={tag}>{tag}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div>
                <div className="mb-1 text-xs text-muted-foreground">点单酒水 Tag</div>
                <Select value={beverageTag} onValueChange={(value) => onBeverageTagChange(value ?? '')}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {selectedCustomer.beverageTags.map((tag) => (
                      <SelectItem key={tag} value={tag}>{tag}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </CardContent>
          </Card>

          <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
            <ListPanel title={`料理推荐 (${recipes.length})`}>
              {recipes.length === 0 && <EmptyRow text="暂无满足点单的料理" />}
              <div className="space-y-2">
                {recipes.map((recipe, index) => (
                  <RecipeRecommendationRow
                    key={`${recipe.recipe.id}-${index}`}
                    recipe={recipe}
                    index={index}
                    ownedIngredientQty={runtimeSets.ownedIngredientQty}
                  />
                ))}
              </div>
            </ListPanel>

            <ListPanel title={`酒水推荐 (${beverages.length})`}>
              {beverages.length === 0 && <EmptyRow text="暂无满足点单的酒水" />}
              <div className="space-y-2">
                {beverages.map((beverage, index) => (
                  <BeverageRecommendationRow
                    key={beverage.beverage.id}
                    beverage={beverage}
                    index={index}
                    ownedBeverageQty={runtimeSets.ownedBeverageQty}
                  />
                ))}
              </div>
            </ListPanel>
          </div>
        </>
      )}
    </div>
  );
}

function ModServicePanel({
  runtime,
  night,
  detectedPlace,
  recommendations,
  recommendationIssues,
  runtimeSets,
  onEnterFocusMode,
}: {
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  detectedPlace: TPlace | null;
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  onEnterFocusMode: () => void;
}) {
  const activeGuests = night?.activeRareGuests ?? [];
  const orders = useMemo(() => sortNightOrders(night?.orders ?? []), [night?.orders]);

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button size="sm" onClick={onEnterFocusMode}>
          稀客订单专注模式
        </Button>
      </div>

      <Card>
        <CardContent className="grid gap-3 p-4 text-sm md:grid-cols-3">
          <InfoLine label="经营场景" value={detectedPlace ?? night?.placeLabel ?? '无经营场景'} />
          <InfoLine label="扫描状态" value={night?.source || '暂无'} />
          <InfoLine label="推荐数据" value={runtime ? '已就绪' : '暂不可用'} />
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <ListPanel title="当前稀客">
          {activeGuests.length === 0 && <EmptyRow text="暂无稀客" />}
          {activeGuests.map((guest) => (
            <div key={`${guest.deskCode}-${guest.guestId}-${guest.source}`} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
              <span className="font-medium">{guest.guestName}</span>
              <span className="text-muted-foreground">桌 {formatDesk(guest.deskCode)} · {guest.source}</span>
            </div>
          ))}
        </ListPanel>

        <ListPanel title="当前稀客点单">
          {orders.length === 0 && <EmptyRow text={night?.error || '暂无点单'} />}
          {orders.map((order) => (
            <div key={`${order.deskCode}-${order.guestId}-${order.foodTagId}-${order.beverageTagId}`} className="border-b py-2 text-sm last:border-b-0">
              <div className="flex items-center justify-between gap-3">
                <span className="font-medium">{order.guestName}</span>
                <span className="text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
              </div>
              <div className="mt-1 flex flex-wrap gap-1.5">
                <Badge variant="outline">料理 {order.foodTag || '无'} ({order.foodTagId})</Badge>
                <Badge variant="outline">酒水 {order.beverageTag || '无'} ({order.beverageTagId})</Badge>
                <Badge variant="secondary">{order.source}</Badge>
              </div>
            </div>
          ))}
        </ListPanel>
      </div>

      {(recommendations.length > 0 || recommendationIssues.length > 0) && (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          runtimeSets={runtimeSets}
        />
      )}
    </div>
  );
}

function ServiceFocusPage({
  recommendations,
  recommendationIssues,
  runtimeSets,
  compact,
  onCompactChange,
  onExit,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  compact: boolean;
  onCompactChange: (value: boolean) => void;
  onExit: () => void;
}) {
  const hasOrders = recommendations.length > 0 || recommendationIssues.length > 0;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-foreground">稀客订单专注模式</h1>
          <p className="mt-1 text-sm text-muted-foreground">只显示当前稀客点单推荐。</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant={compact ? 'default' : 'outline'} onClick={() => onCompactChange(!compact)}>
            {compact ? '常规模式' : '精简模式'}
          </Button>
          <Button size="sm" variant="outline" onClick={onExit}>退出专注模式</Button>
        </div>
      </div>

      {hasOrders ? (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          runtimeSets={runtimeSets}
          compact={compact}
        />
      ) : (
        <EmptyState text="暂无当前稀客点单。检测到稀客点单后，这里会自动显示推荐料理和酒水。" />
      )}
    </div>
  );
}

function CurrentOrderRecommendations({
  recommendations,
  recommendationIssues,
  runtimeSets,
  compact = false,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  compact?: boolean;
}) {
  const rows = useMemo(
    () => [
      ...recommendationIssues.map((issue) => ({ kind: 'issue' as const, order: issue.order, issue })),
      ...recommendations.map((item) => ({ kind: 'recommendation' as const, order: item.order, item })),
    ].sort((left, right) => compareNightOrders(left.order, right.order)),
    [recommendationIssues, recommendations],
  );

  return (
    <ListPanel title="当前点单推荐">
      <div className={compact ? 'space-y-2' : 'space-y-4'}>
        {rows.map((row) => {
          if (row.kind === 'issue') {
            const issue = row.issue;
            return (
              <div
                key={`${issue.order.deskCode}-${issue.order.guestId}-issue`}
                className={compact ? 'rounded-md border border-border p-2 text-xs' : 'rounded-md border border-border p-3 text-sm'}
              >
                <div className="font-medium">{issue.order.guestName} · 桌 {formatDesk(issue.order.deskCode)}</div>
                <div className="mt-1 text-xs text-muted-foreground">{issue.message}</div>
              </div>
            );
          }

          return (
            <OrderRecommendationPanel
              key={`${row.item.order.deskCode}-${row.item.order.guestId}-${row.item.order.foodTagId}-${row.item.order.beverageTagId}`}
              item={row.item}
              runtimeSets={runtimeSets}
              compact={compact}
            />
          );
        })}
      </div>
    </ListPanel>
  );
}

function ModInventoryPanel({
  endpoint,
  apiToken,
  runtimeSets,
  runtimeLoaded,
  onRefresh,
}: {
  endpoint: string;
  apiToken: string;
  runtimeSets: RuntimeSets | null;
  runtimeLoaded: boolean;
  onRefresh: () => Promise<void>;
}) {
  const [search, setSearch] = useState('');
  const [draftQuantities, setDraftQuantities] = useState<Record<string, string>>({});
  const [busyKey, setBusyKey] = useState('');
  const [message, setMessage] = useState('');

  const normalizedSearch = search.trim().toLowerCase();
  const ingredientRows = useMemo(
    () => filterInventoryItems(INGREDIENTS, normalizedSearch),
    [normalizedSearch],
  );
  const beverageRows = useMemo(
    () => filterInventoryItems(BEVERAGES.filter((beverage) => beverage.id >= 0), normalizedSearch),
    [normalizedSearch],
  );

  const applyQuantity = useCallback(async (kind: 'ingredient' | 'beverage', id: number, quantity: number) => {
    const key = inventoryDraftKey(kind, id);
    const targetQuantity = normalizeEditableQuantity(quantity);
    setBusyKey(key);
    setMessage('');

    try {
      const result = await writeInventoryQuantity(endpoint, apiToken, kind, id, targetQuantity);
      if (!result.ok) throw new Error(result.error || '库存修改失败');
      setDraftQuantities((current) => ({ ...current, [key]: String(result.quantity) }));
      setMessage(`${kind === 'ingredient' ? '材料' : '酒水'} #${id}: ${result.previousQuantity} -> ${result.quantity}`);
      await onRefresh();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : String(err));
    } finally {
      setBusyKey('');
    }
  }, [apiToken, endpoint, onRefresh]);

  if (!runtimeLoaded || !runtimeSets) {
    return <RuntimeUnavailable />;
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="grid gap-3 p-4 text-sm lg:grid-cols-[1fr_auto]">
          <div>
            <div className="font-semibold">库存数量修改</div>
            <div className="mt-1 text-xs text-muted-foreground">
              修改会写入当前游戏运行时库存；请在游戏内保存后再退出。经营中修改可能会和实时消耗同时发生。
            </div>
          </div>
          <div className="flex min-w-0 items-center gap-2">
            <Input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="搜索名称或 ID"
              className="w-56"
            />
            <Button size="sm" variant="outline" onClick={onRefresh}>
              <RefreshCw className="size-4" />
              刷新
            </Button>
          </div>
          {message && (
            <div className="lg:col-span-2 text-xs text-muted-foreground">
              {message}
            </div>
          )}
        </CardContent>
      </Card>

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <InventoryEditColumn
          title="材料"
          kind="ingredient"
          items={ingredientRows}
          ownedQty={runtimeSets.ownedIngredientQty}
          draftQuantities={draftQuantities}
          busyKey={busyKey}
          apiToken={apiToken}
          onDraftChange={setDraftQuantities}
          onApply={applyQuantity}
        />
        <InventoryEditColumn
          title="酒水"
          kind="beverage"
          items={beverageRows}
          ownedQty={runtimeSets.ownedBeverageQty}
          draftQuantities={draftQuantities}
          busyKey={busyKey}
          apiToken={apiToken}
          onDraftChange={setDraftQuantities}
          onApply={applyQuantity}
        />
      </div>
    </div>
  );
}

function InventoryEditColumn<TItem extends IIngredient | IBeverage>({
  title,
  kind,
  items,
  ownedQty,
  draftQuantities,
  busyKey,
  apiToken,
  onDraftChange,
  onApply,
}: {
  title: string;
  kind: 'ingredient' | 'beverage';
  items: TItem[];
  ownedQty: Record<number, number>;
  draftQuantities: Record<string, string>;
  busyKey: string;
  apiToken: string;
  onDraftChange: (next: SetStateAction<Record<string, string>>) => void;
  onApply: (kind: 'ingredient' | 'beverage', id: number, quantity: number) => Promise<void>;
}) {
  return (
    <ListPanel title={`${title} (${items.length})`}>
      <div className="space-y-2">
        {items.length === 0 && <EmptyRow text="没有匹配项目" />}
        {items.map((item) => {
          const key = inventoryDraftKey(kind, item.id);
          const quantity = ownedQty[item.id] ?? 0;
          const editable = Boolean(apiToken) && item.id >= 0 && quantity >= 0;
          const draftValue = draftQuantities[key] ?? String(quantity);
          const draftQuantity = normalizeEditableQuantity(Number(draftValue));
          const busy = busyKey === key;

          return (
            <div key={key} className="rounded-md border border-border/80 p-2 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="min-w-0">
                  <div className="truncate font-medium" title={item.name}>{item.name}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    ID {item.id} · 当前 {quantity < 0 ? '无限' : quantity} · 单价 {item.price}
                  </div>
                </div>
                <Input
                  type="number"
                  min={0}
                  max={9999}
                  value={draftValue}
                  onChange={(event) => {
                    const value = event.target.value;
                    onDraftChange((current) => ({ ...current, [key]: value }));
                  }}
                  disabled={!editable || busy}
                  className="h-8 w-24"
                />
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5">
                <Button size="sm" variant="outline" disabled={!editable || busy} onClick={() => onApply(kind, item.id, quantity + 1)}>
                  +1
                </Button>
                <Button size="sm" variant="outline" disabled={!editable || busy} onClick={() => onApply(kind, item.id, quantity + 10)}>
                  +10
                </Button>
                <Button size="sm" variant="outline" disabled={!editable || busy} onClick={() => onApply(kind, item.id, 99)}>
                  99
                </Button>
                <Button size="sm" disabled={!editable || busy} onClick={() => onApply(kind, item.id, draftQuantity)}>
                  {busy ? '修改中' : '应用'}
                </Button>
              </div>
            </div>
          );
        })}
      </div>
    </ListPanel>
  );
}

function ModLogsPanel({ endpoint, apiToken }: { endpoint: string; apiToken: string }) {
  const [settings, setSettings] = useState<LocalApiLogSettings | null>(null);
  const [logs, setLogs] = useState<LocalApiLogs | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);

  const refreshLogs = useCallback(async () => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(true);

    try {
      const nextSettings = await readLogSettings(endpoint, apiToken, abortController.signal);
      setSettings(nextSettings);
      setLogs(nextSettings.logAccessEnabled ? await readLogs(endpoint, apiToken, abortController.signal) : null);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setLoading(false);
    }
  }, [apiToken, endpoint]);

  const updateSettings = useCallback(async (next: { logAccess?: boolean; diagnostics?: boolean }) => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);

    try {
      const nextSettings = await writeLogSettings(endpoint, apiToken, next, abortController.signal);
      setSettings(nextSettings);
      if (!nextSettings.logAccessEnabled) setLogs(null);
      setError('');
      if (nextSettings.logAccessEnabled) {
        const nextLogs = await readLogs(endpoint, apiToken, abortController.signal);
        setLogs(nextLogs);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const openFolder = useCallback(async (target: 'log' | 'diagnostics') => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);

    try {
      const result = await openLogFolder(endpoint, apiToken, target, abortController.signal);
      if (!result.ok) throw new Error(result.error || '打开文件夹失败');
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const visibleLogLines = useMemo(
    () => (logs?.lines ?? []).slice(-MAX_LOG_LINES_IN_VIEW),
    [logs?.lines],
  );
  const configuredLogLimit = settings
    ? `${settings.maxLogLines ?? MAX_LOG_LINES_IN_VIEW} 行 / ${formatBytes(settings.maxLogBytes ?? 0)}`
    : '未知';
  const responseLogLimit = logs
    ? `${logs.maxLines ?? settings?.maxLogLines ?? MAX_LOG_LINES_IN_VIEW} 行 / ${formatBytes(logs.maxBytes ?? settings?.maxLogBytes ?? 0)}`
    : configuredLogLimit;

  useEffect(() => {
    refreshLogs();
    const timer = window.setInterval(refreshLogs, 2000);
    return () => window.clearInterval(timer);
  }, [refreshLogs]);

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="space-y-3 p-4">
          <div className="min-w-0">
            <div className="text-sm font-semibold">Mod 实时日志</div>
            <div className="mt-1 truncate text-xs text-muted-foreground" title={logs?.path || settings?.logOutputPath || endpoint}>
              {error || logs?.path || settings?.logOutputPath || '等待日志响应'}
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button
              size="sm"
              variant={settings?.logAccessEnabled ? 'default' : 'outline'}
              onClick={() => updateSettings({ logAccess: !settings?.logAccessEnabled })}
              disabled={!apiToken || actionLoading}
            >
              <Power className="size-4" />
              {settings?.logAccessEnabled ? '关闭日志读取' : '开启日志读取'}
            </Button>
            <Button
              size="sm"
              variant={settings?.nightBusinessDiagnosticsEnabled ? 'default' : 'outline'}
              onClick={() => updateSettings({ diagnostics: !settings?.nightBusinessDiagnosticsEnabled })}
              disabled={!apiToken || actionLoading}
            >
              <Power className="size-4" />
              {settings?.nightBusinessDiagnosticsEnabled ? '关闭经营诊断' : '开启经营诊断'}
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('log')} disabled={!apiToken || actionLoading}>
              <FolderOpen className="size-4" />
              打开日志文件夹
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('diagnostics')} disabled={!apiToken || actionLoading}>
              <FolderOpen className="size-4" />
              打开诊断文件夹
            </Button>
            <Button size="sm" variant="outline" onClick={refreshLogs} disabled={loading}>
              <RefreshCw className={loading ? 'size-4 animate-spin' : 'size-4'} />
              刷新
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="grid gap-3 p-4 text-sm md:grid-cols-2">
          <InfoLine label="本地 API 授权" value={apiToken ? '已通过启动参数接收' : '未收到 token，请从游戏内按 F8 重新显示窗口'} />
          <InfoLine label="日志读取" value={settings?.logAccessEnabled ? '开启' : '关闭'} />
          <InfoLine label="读取上限" value={responseLogLimit} />
          <InfoLine label="窗口缓存" value={`最多显示 ${MAX_LOG_LINES_IN_VIEW} 行`} />
          <InfoLine label="经营诊断" value={settings?.nightBusinessDiagnosticsEnabled ? '开启' : '关闭'} />
          <InfoLine label="诊断日志目录" value={settings?.nightBusinessDiagnosticsDirectory || '未知'} mono />
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-0">
          <pre className="max-h-[62vh] overflow-auto whitespace-pre-wrap break-words p-4 font-mono text-xs leading-relaxed text-foreground">
            {error
              || logs?.error
              || (!settings?.logAccessEnabled ? '日志读取已关闭。需要排查时点击“开启日志读取”，结束后建议关闭。' : null)
              || (logs?.exists === false ? '未找到 BepInEx/LogOutput.log。' : null)
              || (visibleLogLines.length ? visibleLogLines.join('\n') : '暂无日志内容。')}
          </pre>
        </CardContent>
      </Card>
    </div>
  );
}

function StatusCard({
  label,
  value,
  detail,
  tone,
}: {
  label: string;
  value: string;
  detail: string;
  tone: 'good' | 'bad' | 'neutral';
}) {
  const toneClass = tone === 'good'
    ? 'text-emerald-700 dark:text-emerald-300'
    : tone === 'bad'
      ? 'text-destructive'
      : 'text-foreground';

  return (
    <Card>
      <CardContent className="p-4">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={`mt-1 text-lg font-semibold ${toneClass}`}>{value}</div>
        <div className="mt-1 truncate text-xs text-muted-foreground" title={detail}>{detail}</div>
      </CardContent>
    </Card>
  );
}

function Metric({ label, value }: { label: string; value: number | string }) {
  return (
    <div>
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 text-base font-semibold">{value}</div>
    </div>
  );
}

function InfoLine({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className={`mt-1 truncate text-sm ${mono ? 'font-mono text-xs' : 'font-medium'}`} title={value}>{value}</div>
    </div>
  );
}

function ListPanel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card>
      <CardContent className="p-4">
        <h2 className="mb-2 text-base font-semibold">{title}</h2>
        {children}
      </CardContent>
    </Card>
  );
}

function LowStockColumn({
  title,
  entries,
}: {
  title: string;
  entries: LowStockEntry[];
}) {
  return (
    <div>
      <h3 className="mb-1 text-sm font-medium">{title}</h3>
      {entries.length === 0 && <EmptyRow text="暂无库存数据" />}
      {entries.map((item) => (
        <div key={item.id} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
          <span>{item.name}</span>
          <span className="text-muted-foreground">{item.qty}</span>
        </div>
      ))}
    </div>
  );
}

function TagSummary({
  tags,
  cancelledTags,
}: {
  tags: string[];
  cancelledTags: string[];
}) {
  if (tags.length === 0 && cancelledTags.length === 0) return null;

  return (
    <div className="mt-1 flex flex-wrap gap-1">
      {tags.map((tag) => <TagBadge key={tag} tag={tag} variant="default" />)}
      {cancelledTags.map((tag) => (
        <Badge key={`cancelled-${tag}`} variant="outline" className="text-muted-foreground">
          已抵消 {tag}
        </Badge>
      ))}
    </div>
  );
}

function EmptyRow({ text }: { text: string }) {
  return <div className="py-6 text-center text-sm text-muted-foreground">{text}</div>;
}

function EmptyState({ text }: { text: string }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-sm text-muted-foreground">{text}</CardContent>
    </Card>
  );
}

function RuntimeUnavailable() {
  return <EmptyState text="尚未读取到游戏实时数据。请确认游戏已加载存档，且 Mod 本地 API 已连接。" />;
}

function PlaceToolbar({
  selectedPlace,
  detectedPlace,
  onPlaceChange,
  onFollowDetectedPlace,
}: {
  selectedPlace: TPlace | null;
  detectedPlace: TPlace | null;
  onPlaceChange: (place: TPlace) => void;
  onFollowDetectedPlace: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <RegionSelector value={selectedPlace} onChange={onPlaceChange} />
      {detectedPlace && (
        <Button size="sm" variant="outline" onClick={onFollowDetectedPlace}>
          跟随经营场景: {detectedPlace}
        </Button>
      )}
    </div>
  );
}

function NormalRecipeRow({
  recipe,
  index,
  ownedIngredientQty,
}: {
  recipe: INormalRecipeResult;
  index: number;
  ownedIngredientQty: Record<number, number>;
}) {
  return (
    <div className="rounded-md border border-border/80 p-2 text-sm">
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-muted-foreground">#{index + 1}</span>
        <span className="font-medium">{recipe.recipe.name}</span>
        <Badge variant="secondary">{recipe.recipe.cooker || '未知厨具'}</Badge>
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        分数 {recipe.totalCoverage} · 成本 {recipe.ingredientCost} · 利润 {recipe.profit} · 价格 {recipe.recipe.price}
      </div>
      <div className="mt-1 flex flex-wrap gap-1">
        {recipe.matchedTags.map((tag) => <TagBadge key={tag} tag={tag} variant="matched" />)}
      </div>
      <div className="mt-1">
        <CustomerScoreBadges scores={recipe.customerScores} />
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        基础配方: {formatIngredientNamesWithQty(recipe.recipe.ingredients, ownedIngredientQty) || '无'}
      </div>
    </div>
  );
}

function NormalBeverageRow({
  beverage,
  index,
  ownedBeverageQty,
}: {
  beverage: INormalBeverageResult;
  index: number;
  ownedBeverageQty: Record<number, number>;
}) {
  return (
    <div className="rounded-md border border-border/80 p-2 text-sm">
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-muted-foreground">#{index + 1}</span>
        <span className="font-medium">
          {beverage.beverage.name}{formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
        </span>
        <span className="text-primary">¥{beverage.beverage.price}</span>
      </div>
      <div className="mt-1 text-xs text-muted-foreground">分数 {beverage.totalCoverage}</div>
      <div className="mt-1 flex flex-wrap gap-1">
        {beverage.beverage.tags.map((tag) => (
          <TagBadge key={tag} tag={tag} variant={beverage.matchedTags.includes(tag) ? 'matched' : 'default'} />
        ))}
      </div>
      <div className="mt-1">
        <CustomerScoreBadges scores={beverage.customerScores} />
      </div>
    </div>
  );
}

function OrderRecommendationPanel({
  item,
  runtimeSets,
  compact = false,
}: {
  item: OrderRecommendation;
  runtimeSets: RuntimeSets | null;
  compact?: boolean;
}) {
  return (
    <div className={compact ? 'rounded-md border border-border p-2' : 'rounded-md border border-border p-3'}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-sm font-semibold">{item.customer.name} · 桌 {formatDesk(item.order.deskCode)}</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <Badge variant="outline">料理 {item.order.foodTag || '无'}</Badge>
            <Badge variant="outline">酒水 {item.order.beverageTag || '无'}</Badge>
            <Badge variant="secondary">{item.order.source}</Badge>
          </div>
        </div>
      </div>

      <div className={compact ? 'mt-2 grid gap-2 lg:grid-cols-2' : 'mt-3 grid gap-4 lg:grid-cols-2'}>
        <div>
          <h3 className={compact ? 'mb-1 text-xs font-semibold' : 'mb-2 text-sm font-semibold'}>推荐料理</h3>
          {item.recipes.length === 0 && <EmptyRow text="暂无满足点单的料理" />}
          <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
            {item.recipes.map((recipe, index) => (
              <RecipeRecommendationRow
                key={`${recipe.recipe.id}-${index}`}
                recipe={recipe}
                index={index}
                ownedIngredientQty={runtimeSets?.ownedIngredientQty ?? {}}
                compact={compact}
              />
            ))}
          </div>
        </div>

        <div>
          <h3 className={compact ? 'mb-1 text-xs font-semibold' : 'mb-2 text-sm font-semibold'}>推荐酒水</h3>
          {item.beverages.length === 0 && <EmptyRow text="暂无满足点单的酒水" />}
          <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
            {item.beverages.map((beverage, index) => (
              <BeverageRecommendationRow
                key={beverage.beverage.id}
                beverage={beverage}
                index={index}
                ownedBeverageQty={runtimeSets?.ownedBeverageQty ?? {}}
                compact={compact}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function RecipeRecommendationRow({
  recipe,
  index,
  ownedIngredientQty,
  compact = false,
}: {
  recipe: IRareRecipeResult;
  index: number;
  ownedIngredientQty: Record<number, number>;
  compact?: boolean;
}) {
  const totalCost = recipe.baseCost + recipe.extraCost;
  const extras = recipe.extraIngredients.length === 0
    ? '不加料'
    : recipe.extraIngredients.map((ingredient) => `+${formatIngredientWithQty(ingredient.name, ownedIngredientQty)}`).join(', ');

  return (
    <div className={compact ? 'rounded-md border border-border/80 p-1.5 text-xs' : 'rounded-md border border-border/80 p-2 text-sm'}>
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-muted-foreground">#{index + 1}</span>
        <span className="font-medium">{recipe.recipe.name}</span>
        <Badge variant="secondary">{RATING_LABELS[recipe.rating]}</Badge>
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        分数 {recipe.foodScore} · 成本 {totalCost} · {extras}
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        厨具: {recipe.recipe.cooker || '未知'} · 基础配方: {formatIngredientNamesWithQty(recipe.recipe.ingredients, ownedIngredientQty) || '无'}
      </div>
      {!compact && <TagSummary tags={recipe.allTags} cancelledTags={recipe.cancelledTags} />}
    </div>
  );
}

function BeverageRecommendationRow({
  beverage,
  index,
  ownedBeverageQty,
  compact = false,
}: {
  beverage: IRareBeverageResult;
  index: number;
  ownedBeverageQty: Record<number, number>;
  compact?: boolean;
}) {
  return (
    <div className={compact ? 'rounded-md border border-border/80 p-1.5 text-xs' : 'rounded-md border border-border/80 p-2 text-sm'}>
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-muted-foreground">#{index + 1}</span>
        <span className="font-medium">
          {beverage.beverage.name}{formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
        </span>
        {beverage.meetsRequiredBev && <Badge variant="secondary">满足点单</Badge>}
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        分数 {beverage.bevScore} · 价格 {beverage.beverage.price}
        {!compact && beverage.matchedTags.length > 0 ? ` · Tag: ${beverage.matchedTags.join(', ')}` : ''}
      </div>
    </div>
  );
}

async function readSnapshot(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiSnapshot> {
  return readLocalApiJson<LocalApiSnapshot>(endpoint, apiToken, '/snapshot', signal);
}

async function readLogs(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiLogs> {
  return readLocalApiJson<LocalApiLogs>(endpoint, apiToken, '/logs', signal);
}

async function readLogSettings(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiLogSettings> {
  return readLocalApiJson<LocalApiLogSettings>(endpoint, apiToken, '/logs/settings', signal);
}

async function writeLogSettings(
  endpoint: string,
  apiToken: string,
  next: { logAccess?: boolean; diagnostics?: boolean },
  signal: AbortSignal,
): Promise<LocalApiLogSettings> {
  const params = new URLSearchParams();
  if (typeof next.logAccess === 'boolean') params.set('logAccess', String(next.logAccess));
  if (typeof next.diagnostics === 'boolean') params.set('diagnostics', String(next.diagnostics));
  return readLocalApiJson<LocalApiLogSettings>(endpoint, apiToken, `/logs/config?${params.toString()}`, signal);
}

async function openLogFolder(
  endpoint: string,
  apiToken: string,
  target: 'log' | 'diagnostics',
  signal: AbortSignal,
): Promise<LocalApiFolderResponse> {
  return readLocalApiJson<LocalApiFolderResponse>(endpoint, apiToken, `/logs/open-folder?target=${target}`, signal);
}

async function writeInventoryQuantity(
  endpoint: string,
  apiToken: string,
  itemType: 'ingredient' | 'beverage',
  itemId: number,
  quantity: number,
): Promise<InventoryEditResponse> {
  const params = new URLSearchParams({
    type: itemType,
    id: String(itemId),
    qty: String(normalizeEditableQuantity(quantity)),
  });
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), 3200);

  try {
    return await readLocalApiJson<InventoryEditResponse>(
      endpoint,
      apiToken,
      `/inventory/set?${params.toString()}`,
      abortController.signal,
    );
  } finally {
    window.clearTimeout(timeoutId);
  }
}

async function readLocalApiJson<T>(endpoint: string, apiToken: string, path: string, signal: AbortSignal): Promise<T> {
  const targetEndpoint = `${endpoint}${path}`;
  if (isTauriRuntime()) {
    const { invoke } = await import('@tauri-apps/api/core');
    const payload = await invoke<string>('fetch_snapshot', { endpoint: targetEndpoint, token: apiToken });
    return JSON.parse(payload) as T;
  }

  const headers = new Headers();
  if (apiToken) headers.set('X-Mystia-Steward-Companion-Token', apiToken);
  const response = await fetch(targetEndpoint, {
    cache: 'no-store',
    headers,
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  return await response.json() as T;
}

function buildRuntimeSets(runtime: RecommendationStateSnapshot | null): RuntimeSets | null {
  if (!runtime) return null;
  const ingredientIds = new Set(runtime.availableIngredientIds);
  const allIngredientIds = (allIngredients as IIngredient[]).map((ingredient) => ingredient.id);
  const unavailableIngredientIds = new Set(allIngredientIds.filter((id) => !ingredientIds.has(id)));

  return {
    recipeIds: new Set(runtime.availableRecipeIds),
    beverageIds: new Set(runtime.availableBeverageIds),
    ingredientIds,
    unavailableIngredientIds,
    ownedIngredientQty: normalizeOwnedIngredientQty(runtime.ownedIngredientQty),
    ownedBeverageQty: normalizeOwnedIngredientQty(runtime.ownedBeverageQty ?? {}),
  };
}

function sortNightOrders(orders: NightBusinessOrder[]): NightBusinessOrder[] {
  return [...orders].sort(compareNightOrders);
}

function compareNightOrders(left: NightBusinessOrder, right: NightBusinessOrder): number {
  const leftSeenAt = getOrderSeenTime(left);
  const rightSeenAt = getOrderSeenTime(right);
  if (leftSeenAt !== rightSeenAt) return leftSeenAt - rightSeenAt;
  if (left.deskCode !== right.deskCode) return left.deskCode - right.deskCode;
  return left.guestName.localeCompare(right.guestName, 'zh-Hans-CN');
}

function getOrderSeenTime(order: NightBusinessOrder): number {
  const value = order.firstSeenAtUtc ?? order.lastSeenAtUtc;
  if (!value) return Number.MAX_SAFE_INTEGER;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : Number.MAX_SAFE_INTEGER;
}

function filterInventoryItems<TItem extends IIngredient | IBeverage>(items: TItem[], normalizedSearch: string): TItem[] {
  const rows = normalizedSearch
    ? items.filter((item) => item.name.toLowerCase().includes(normalizedSearch) || String(item.id).includes(normalizedSearch))
    : items;
  return rows
    .filter((item) => item.id >= 0)
    .sort((a, b) => a.id - b.id);
}

function inventoryDraftKey(kind: 'ingredient' | 'beverage', itemId: number) {
  return `${kind}:${itemId}`;
}

function normalizeEditableQuantity(value: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(9999, Math.trunc(value)));
}

function buildOrderRecommendations(
  orders: NightBusinessOrder[],
  runtime: RecommendationStateSnapshot | null | undefined,
  rareCustomersById: Map<number, ICustomerRare>,
  cache: Map<string, CachedRecommendation>,
): { recommendations: OrderRecommendation[]; recommendationIssues: RecommendationIssue[] } {
  if (orders.length === 0) return { recommendations: [], recommendationIssues: [] };
  const sortedOrders = sortNightOrders(orders);
  if (!runtime) {
    return {
      recommendations: [],
      recommendationIssues: sortedOrders.map((order) => ({ order, message: '运行时推荐数据暂不可用。' })),
    };
  }

  const runtimeSets = buildRuntimeSets(runtime);
  if (!runtimeSets) return { recommendations: [], recommendationIssues: [] };

  const stateSignature = buildRecommendationStateSignature(runtime);
  const recommendations: OrderRecommendation[] = [];
  const recommendationIssues: RecommendationIssue[] = [];

  for (const order of sortedOrders) {
    const customer = findRareCustomer(order, rareCustomersById);
    const foodTag = order.foodTag.trim();
    const beverageTag = order.beverageTag.trim();

    if (!customer) {
      recommendationIssues.push({ order, message: '无法把该稀客映射到本地稀客数据。' });
      continue;
    }
    if (!foodTag || !beverageTag) {
      recommendationIssues.push({ order, message: '该点单缺少料理 Tag 或酒水 Tag。' });
      continue;
    }

    const cacheKey = `${stateSignature}|${customer.id}|${foodTag}|${beverageTag}`;
    let cached = cache.get(cacheKey);
    if (!cached) {
      const recipes = rankRecipesForRare(
        customer,
        foodTag,
        beverageTag,
        runtimeSets.recipeIds,
        runtimeSets.ingredientIds,
        new Set<number>(),
        runtime.popularFoodTag,
        runtime.popularHateFoodTag,
        4,
        runtimeSets.ownedIngredientQty,
        runtime.famousShopEnabled,
      )
        .sort(compareRareRecipesForService)
        .slice(0, MAX_RECOMMENDATION_ROWS);

      const beverages = rankBeveragesForRare(customer, beverageTag, runtimeSets.beverageIds)
        .sort(compareRareBeveragesForService)
        .slice(0, MAX_RECOMMENDATION_ROWS);

      cached = { customer, recipes, beverages };
      cache.set(cacheKey, cached);
      trimRecommendationCache(cache);
    }

    recommendations.push({ order, ...cached });
  }

  return { recommendations, recommendationIssues };
}

function findRareCustomer(order: NightBusinessOrder, rareCustomersById: Map<number, ICustomerRare>) {
  if (order.guestId != null) {
    const byId = rareCustomersById.get(order.guestId);
    if (byId) return byId;
  }

  return [...rareCustomersById.values()].find((customer) => customer.name === order.guestName) ?? null;
}

function compareNormalRecipesForMod(a: INormalRecipeResult, b: INormalRecipeResult) {
  if (a.totalCoverage !== b.totalCoverage) return b.totalCoverage - a.totalCoverage;
  if (a.ingredientCost !== b.ingredientCost) return b.ingredientCost - a.ingredientCost;
  return a.recipe.id - b.recipe.id;
}

function compareNormalBeveragesForMod(a: INormalBeverageResult, b: INormalBeverageResult) {
  if (a.totalCoverage !== b.totalCoverage) return b.totalCoverage - a.totalCoverage;
  if (a.beverage.price !== b.beverage.price) return b.beverage.price - a.beverage.price;
  return a.beverage.id - b.beverage.id;
}

function compareRareRecipesForService(a: IRareRecipeResult, b: IRareRecipeResult) {
  if (a.meetsRequiredFood !== b.meetsRequiredFood) return a.meetsRequiredFood ? -1 : 1;
  if (a.foodScore !== b.foodScore) return b.foodScore - a.foodScore;
  const aCost = a.baseCost + a.extraCost;
  const bCost = b.baseCost + b.extraCost;
  if (aCost !== bCost) return bCost - aCost;
  return a.recipe.id - b.recipe.id;
}

function compareRareBeveragesForService(a: IRareBeverageResult, b: IRareBeverageResult) {
  if (a.meetsRequiredBev !== b.meetsRequiredBev) return a.meetsRequiredBev ? -1 : 1;
  if (a.bevScore !== b.bevScore) return b.bevScore - a.bevScore;
  if (a.beverage.price !== b.beverage.price) return b.beverage.price - a.beverage.price;
  return a.beverage.id - b.beverage.id;
}

function normalizeOwnedIngredientQty(ownedIngredientQty: Record<string, number>): Record<number, number> {
  return Object.fromEntries(
    Object.entries(ownedIngredientQty).map(([id, qty]) => [Number(id), qty]),
  ) as Record<number, number>;
}

interface LowStockEntry {
  id: number;
  name: string;
  qty: number;
}

function buildLowStockEntries(
  qtyById: Record<string, number>,
  nameById: Map<number, string>,
  limit = 8,
): LowStockEntry[] {
  return Object.entries(qtyById)
    .map(([id, qty]) => {
      const numericId = Number(id);
      return {
        id: numericId,
        name: nameById.get(numericId) ?? `#${id}`,
        qty,
      };
    })
    .filter((item) => Number.isFinite(item.id) && item.qty >= 0)
    .sort((a, b) => a.qty - b.qty || a.id - b.id)
    .slice(0, limit);
}

function buildRecommendationStateSignature(runtime: RecommendationStateSnapshot) {
  const ownedQty = Object.entries(runtime.ownedIngredientQty)
    .sort(([a], [b]) => Number(a) - Number(b))
    .map(([id, qty]) => `${id}:${qty}`)
    .join(',');

  return [
    runtime.availableRecipeIds.join(','),
    runtime.availableBeverageIds.join(','),
    runtime.availableIngredientIds.join(','),
    ownedQty,
    runtime.popularFoodTag ?? '',
    runtime.popularHateFoodTag ?? '',
    runtime.famousShopEnabled ? '1' : '0',
  ].join('|');
}

function trimRecommendationCache(cache: Map<string, CachedRecommendation>) {
  if (cache.size <= 24) return;
  const keysToDelete = [...cache.keys()].slice(0, cache.size - 24);
  for (const key of keysToDelete) cache.delete(key);
}

function normalizeEndpoint(value: string) {
  const trimmed = value.trim() || DEFAULT_ENDPOINT;
  const withProtocol = /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`;
  try {
    const url = new URL(withProtocol);
    if (url.hostname === 'localhost') url.hostname = '127.0.0.1';
    return url.toString().replace(/\/+$/, '');
  } catch {
    return trimmed.replace(/\/+$/, '');
  }
}

function normalizePlace(value: string | null | undefined): TPlace | null {
  return ALL_PLACES.includes(value as TPlace) ? value as TPlace : null;
}

function isOrderableRareFoodTag(tag: string): boolean {
  return !NON_ORDERABLE_RARE_FOOD_TAGS.has(tag);
}

function readStoredTab(): ModTab {
  const value = readMigratedStorage(TAB_STORAGE_KEY, LEGACY_TAB_STORAGE_KEY, '');
  if (value === 'settings') return 'overview';
  return value === 'overview' || value === 'normal' || value === 'rare' || value === 'service' || value === 'inventory' || value === 'logs'
    ? value
    : 'service';
}

function readMigratedStorage(key: string, legacyKey: string, fallback: string) {
  const value = localStorage.getItem(key);
  if (value !== null) return value;

  const legacyValue = localStorage.getItem(legacyKey);
  if (legacyValue === null) return fallback;

  localStorage.setItem(key, legacyValue);
  localStorage.removeItem(legacyKey);
  return legacyValue;
}

async function toggleCompanionFocus() {
  if (!isTauriRuntime()) return;

  try {
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('toggle_companion_focus');
  } catch {
    // Browser mode and older companion builds do not expose this command.
  }
}

function formatTime(date: Date) {
  return date.toLocaleTimeString('zh-CN', { hour12: false });
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return '未知';
  if (value >= 1024 * 1024) return `${Math.round(value / 1024 / 1024)} MiB`;
  return `${Math.round(value / 1024)} KiB`;
}

function formatDesk(deskCode: number) {
  return deskCode >= 0 ? String(deskCode + 1) : String(deskCode);
}

function formatIngredientNamesWithQty(names: string[], ownedIngredientQty: Record<number, number>) {
  return names.map((name) => formatIngredientWithQty(name, ownedIngredientQty)).join(', ');
}

function formatIngredientWithQty(name: string, ownedIngredientQty: Record<number, number>) {
  const id = INGREDIENT_ID_BY_NAME.get(name);
  return `${name}${formatQtySuffix(id == null ? undefined : ownedIngredientQty[id])}`;
}

function formatQtySuffix(qty: number | undefined) {
  return `(${qty == null || qty < 0 ? '?' : qty})`;
}
