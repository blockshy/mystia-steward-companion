import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/menu_item_content.dart';
import '../ui/steward_style.dart';

/// 搜索只规范化显示文字，不改变目录编号或写入身份。中文输入法常见的全角
/// ASCII、全角空格、大小写和首尾空白统一处理；原始收藏和目录保持原样。
String normalizeRecommendationSearch(String text) => String.fromCharCodes(
  text.runes.map(
    (rune) => rune == 0x3000
        ? 0x20
        : rune >= 0xff01 && rune <= 0xff5e
        ? rune - 0xfee0
        : rune,
  ),
).trim().toLowerCase();

/// 收藏搜索覆盖保存的客人/点单/编号，以及唯一目录解析出的名称、基础配方和
/// 追加食材。目录丢失时保留编号与保存名称，不能用同名条目冒充已解析的实体。
bool favoriteMatchesSearch(
  WorkbenchModel model,
  DataMap entry,
  String query, {
  required bool food,
}) {
  final needle = normalizeRecommendationSearch(query);
  if (needle.isEmpty) return true;
  DataMap unique(String kind, Object? id) {
    final entries = model
        .catalogItems(kind)
        .where((row) => row['id'] == id)
        .take(2)
        .toList();
    return entries.length == 1 ? entries.single : const {};
  }

  final entity = unique(
    food ? 'recipes' : 'beverages',
    entry[food ? 'recipeId' : 'beverageId'],
  );
  final customer = unique('rareCustomers', entry['customerId']);
  final searchable = <Object?>[
    entry['id'],
    entry['customerId'],
    entry['customerName'],
    customer['name'],
    entry[food ? 'foodTag' : 'beverageTag'],
    entry[food ? 'recipeId' : 'beverageId'],
    entity['name'],
    ...values(entity[food ? 'positiveTags' : 'tags']),
    if (food) ...[
      ...values(entity['ingredients']),
      for (final id in values(entry['extraIngredientIds'])) ...[
        id,
        unique('ingredients', id)['name'],
      ],
    ],
  ];
  return searchable
      .where((value) => value != null)
      .any((value) => normalizeRecommendationSearch('$value').contains(needle));
}

/// 地区客群是完整目录浏览，不从前八条推荐的命中情况反推喜好。使用独立只读
/// 弹层保留主列表空间；所有标签完整换行，窄屏和大字号由同一滚动轴承载。
Future<void> showNormalCustomerPreferences(
  BuildContext context,
  WorkbenchModel model,
  String place,
) => showFDialog<void>(
  context: context,
  builder: (context, style, animation) => FDialog(
    animation: MediaQuery.disableAnimationsOf(context) ? null : animation,
    builder: (context, _) {
      final media = MediaQuery.of(context);
      final customers = model
          .catalogItems('normalCustomers')
          .where((entry) => values(entry['places']).contains(place))
          .toList();
      return SizedBox(
        width: 520,
        height:
            (media.size.height -
                media.padding.vertical -
                media.viewInsets.bottom) *
            .76,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text('$place · 客群偏好', style: StewardText.sectionTitle),
                ),
                StewardIconButton(
                  label: '关闭客群偏好',
                  icon: FLucideIcons.x,
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
            const SizedBox(height: 12),
            Expanded(
              child: ListView(
                children: [
                  for (final customer in customers)
                    Section(
                      label(customer['name']),
                      [
                        MenuTagGroup(
                          '料理',
                          values(customer['positiveTags'])
                              .whereType<String>()
                              .toList(),
                          kind: OrderTagKind.success,
                        ),
                        MenuTagGroup(
                          '酒水',
                          values(customer['beverageTags'])
                              .whereType<String>()
                              .toList(),
                          kind: OrderTagKind.ingredient,
                        ),
                      ],
                      titleContent: EntityName(
                        label(customer['name']),
                        kind: EntityImageKind.normalGroup,
                        id: entityImageId(customer['id']),
                        fallback: FLucideIcons.users,
                        style: StewardText.itemTitle,
                      ),
                    ),
                ],
              ),
            ),
          ],
        ),
      );
    },
  ),
);
