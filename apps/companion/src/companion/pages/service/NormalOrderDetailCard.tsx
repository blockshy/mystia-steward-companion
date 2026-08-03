import { memo } from 'react';
import { Badge } from '@/components/ui-kit';
import { RecommendationMetaBadge } from '@/components/RecommendationItem';
import { TagPill, TagPillGroup } from '@/components/recommendation/TagPillGroup';
import type { NormalOrderDetailPlan, NormalOrderFoodDetail } from '@/companion/domain/normal-order-details';
import { formatDesk, formatIngredientNamesWithQty, formatIngredientWithQty, formatQtySuffix } from '@/companion/formatters';
import { OrderTraceBadge } from '@/companion/pages/service/ServiceContextPanels';
import { ServiceOrderCardFrame } from '@/companion/pages/service/ServiceOrderPresentation';

interface NormalOrderDetailCardProps {
  plan: NormalOrderDetailPlan;
  ownedIngredientQty: Record<number, number>;
  ownedBeverageQty: Record<number, number>;
  ingredientIdByName: Map<string, number>;
  showDebugDetails: boolean;
}

export const NormalOrderDetailCard = memo(function NormalOrderDetailCard({
  plan,
  ownedIngredientQty,
  ownedBeverageQty,
  ingredientIdByName,
  showDebugDetails,
}: NormalOrderDetailCardProps) {
  const { order } = plan;
  const originalFoodText = `${plan.originalFood.name || `#${order.foodId}`}`;
  const originalBeverageText = `${plan.originalBeverage.name || `#${order.beverageId}`}`;
  const executionLabel = plan.usesSpecialExecution ? '执行方案' : '基础方案';
  const hasStatusBadges = Boolean(
    order.specialBusinessRoleLabel
      || plan.hasExecutionOverride
      || order.hasServedFood
      || order.hasServedBeverage
      || (order.readyToEvaluate && !order.hasEvaluated)
      || order.hasEvaluated
      || order.canAutomate === false
      || showDebugDetails,
  );

  return (
    <ServiceOrderCardFrame
      optimizeRendering
      title={(
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="font-medium" title={order.guestName || '普客'}>
            {order.guestName || '普客'}
          </span>
          <span className="font-normal text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
          <OrderTraceBadge traceId={order.traceId} />
        </div>
      )}
      subtitle={<>原订单：料理 {originalFoodText} / 酒水 {originalBeverageText}</>}
      badges={hasStatusBadges
        ? (
            <>
              {order.specialBusinessRoleLabel && <Badge variant="secondary">{order.specialBusinessRoleLabel}</Badge>}
              {plan.hasExecutionOverride && <Badge variant="secondary">含加料方案</Badge>}
              {order.hasServedFood && <Badge variant="secondary">已有料理</Badge>}
              {order.hasServedBeverage && <Badge variant="secondary">已有酒水</Badge>}
              {order.readyToEvaluate && !order.hasEvaluated && <Badge variant="secondary">待评价</Badge>}
              {order.hasEvaluated && <Badge variant="secondary">已评价</Badge>}
              {order.canAutomate === false && <Badge variant="outline">暂不可自动处理</Badge>}
              {showDebugDetails && <Badge variant="secondary">{order.source}</Badge>}
            </>
          )
        : undefined}
    >
      <div className="mt-2 grid gap-2 lg:grid-cols-2">
        <div className="steward-data-row px-2 py-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-medium">{executionLabel} · 料理</span>
            <Badge variant={plan.usesSpecialExecution ? 'secondary' : 'outline'}>
              {plan.executionFood.name}
            </Badge>
          </div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <RecommendationMetaBadge label="厨具" value={plan.executionFood.cookerName || '未知'} tone="cooker" />
            <RecommendationMetaBadge
              label="基础配方"
              value={formatBaseRecipe(plan.executionFood, ownedIngredientQty, ingredientIdByName)}
              tone="base"
            />
            <RecommendationMetaBadge
              label="加料"
              value={formatExtraIngredients(plan.executionFood, ownedIngredientQty, ingredientIdByName)}
              tone={plan.executionFood.extraIngredients.length > 0 ? 'extra' : 'neutral'}
            />
          </div>
          <FoodTagDetails food={plan.executionFood} />
        </div>

        <div className="steward-data-row px-2 py-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-medium">{executionLabel} · 酒水</span>
            <Badge variant={plan.usesSpecialExecution ? 'secondary' : 'outline'}>
              {plan.executionBeverage.name}{formatQtySuffix(ownedBeverageQty[plan.executionBeverage.beverageId])}
            </Badge>
          </div>
          <TagPillGroup tags={plan.executionBeverage.activeTags} className="mt-2" />
          {plan.executionBeverage.suppressedTags.length > 0 && (
            <div className="mt-1 flex flex-wrap gap-1">
              {plan.executionBeverage.suppressedTags.map((tag) => (
                <TagPill key={`bev-suppressed-${tag}`} tone="suppressed">
                  已抵消 {tag}
                </TagPill>
              ))}
            </div>
          )}
        </div>
      </div>

      {plan.executionReason && (
        <div className="mt-2 text-xs text-muted-foreground">
          规则：{plan.executionReason}
        </div>
      )}
      {plan.selectionMessage && (
        <div className="mt-2 text-xs text-muted-foreground">
          {plan.selectionMessage}
        </div>
      )}
      {order.canAutomate === false && order.actionBlockReason && (
        <div className="mt-2 text-xs text-muted-foreground">
          {order.actionBlockReason}
        </div>
      )}
    </ServiceOrderCardFrame>
  );
}, areNormalOrderDetailCardPropsEqual);

function areNormalOrderDetailCardPropsEqual(
  previous: NormalOrderDetailCardProps,
  next: NormalOrderDetailCardProps,
): boolean {
  if (previous.plan !== next.plan) return false;
  if (previous.showDebugDetails !== next.showDebugDetails) return false;
  return hasSameDisplayedIngredientQty(previous, next) && hasSameDisplayedBeverageQty(previous, next);
}

function hasSameDisplayedIngredientQty(
  previous: NormalOrderDetailCardProps,
  next: NormalOrderDetailCardProps,
): boolean {
  const ingredientIds = collectDisplayedIngredientIds(previous.plan.executionFood, previous.ingredientIdByName);
  for (const id of collectDisplayedIngredientIds(next.plan.executionFood, next.ingredientIdByName)) {
    ingredientIds.add(id);
  }
  for (const id of ingredientIds) {
    if ((previous.ownedIngredientQty[id] ?? 0) !== (next.ownedIngredientQty[id] ?? 0)) return false;
  }
  return true;
}

function hasSameDisplayedBeverageQty(
  previous: NormalOrderDetailCardProps,
  next: NormalOrderDetailCardProps,
): boolean {
  const beverageId = previous.plan.executionBeverage.beverageId;
  if (beverageId !== next.plan.executionBeverage.beverageId) return false;
  return (previous.ownedBeverageQty[beverageId] ?? 0) === (next.ownedBeverageQty[beverageId] ?? 0);
}

function collectDisplayedIngredientIds(
  food: NormalOrderFoodDetail,
  ingredientIdByName: Map<string, number>,
): Set<number> {
  const ids = new Set<number>();
  for (const ingredient of food.extraIngredients) {
    ids.add(ingredient.id);
  }
  for (const name of food.baseIngredientNames) {
    const id = ingredientIdByName.get(name);
    if (typeof id === 'number') ids.add(id);
  }
  return ids;
}

function FoodTagDetails({ food }: { food: NormalOrderFoodDetail }) {
  if (food.activeTags.length === 0 && food.targetTags.length === 0 && food.suppressedTags.length === 0) return null;

  return (
    <div className="mt-2 space-y-1">
      <TagPillGroup tags={food.activeTags} matchedTags={food.targetTags} />
      {food.targetTags.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {food.targetTags.map((tag) => (
            <TagPill key={`target-${tag}`} tone="match">
              目标 {tag}
            </TagPill>
          ))}
        </div>
      )}
      {food.suppressedTags.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {food.suppressedTags.map((tag) => (
            <TagPill key={`suppressed-${tag}`} tone="suppressed">
              已抵消 {tag}
            </TagPill>
          ))}
        </div>
      )}
    </div>
  );
}

function formatBaseRecipe(
  food: NormalOrderFoodDetail,
  ownedIngredientQty: Record<number, number>,
  ingredientIdByName: Map<string, number>,
): string {
  return formatIngredientNamesWithQty(food.baseIngredientNames, ownedIngredientQty, ingredientIdByName) || '未知';
}

function formatExtraIngredients(
  food: NormalOrderFoodDetail,
  ownedIngredientQty: Record<number, number>,
  ingredientIdByName: Map<string, number>,
): string {
  if (food.extraIngredients.length === 0) return '不加料';
  return food.extraIngredients
    .map((ingredient) => formatIngredientWithQty(ingredient.name, ownedIngredientQty, ingredientIdByName))
    .join(', ');
}
