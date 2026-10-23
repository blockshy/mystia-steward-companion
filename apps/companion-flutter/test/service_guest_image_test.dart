import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/service_menu_adapter.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

void main() {
  testWidgets('真实订单标题传递复制稀客身份，未知普通订单保留通用图标', (tester) async {
    final theme = stewardTheme(dark: false, touch: false);
    for (final known in [true, false]) {
      // 直接渲染生产标题，而非仅调用 helper，防止标题接线遗漏仍按普通订单标签选图片。
      await tester.pumpWidget(
        material.MaterialApp(
          theme: theme.toApproximateMaterialTheme(),
          home: FTheme(
            data: theme,
            child: Center(
              child: SizedBox(
                width: 450,
                child: ServiceOrderHeading({
                  'order': {
                    'deskCode': 0,
                    'guestId': 7,
                    'guestName': '合成复制稀客',
                    if (known) 'guestImageKind': 'rare-guest',
                    if (known) 'guestImageId': 27,
                  },
                }, rare: false),
              ),
            ),
          ),
        ),
      );
      await tester.pump();
      final image = tester.widget<EntityImage>(
        find.descendant(
          of: find.byType(ServiceOrderHeading),
          matching: find.byType(EntityImage),
        ),
      );
      expect(
        image.kind,
        known ? EntityImageKind.rareGuest : EntityImageKind.normalGroup,
      );
      expect(image.id, known ? 27 : null);
      expect(find.text('1 桌 · 合成复制稀客'), findsOneWidget);
      expect(tester.takeException(), isNull);
    }
  });

  test('普通订单中的复制稀客使用显式稀客图片身份，不复用裸客群ID', () {
    final image = serviceGuestImage({
      'guestId': 7,
      'runtimeGuestId': 9007,
      'guestName': '同名客人',
      'guestImageKind': 'rare-guest',
      'guestImageId': 27,
    }, rare: false);
    expect(image.kind, EntityImageKind.rareGuest);
    expect(image.id, 27);
  });

  test('同数字客群与稀客独立，特殊经营角色不覆盖已确认图片身份', () {
    final image = serviceGuestImage({
      'guestId': 7,
      'guestImageKind': 'normal-group',
      'guestImageId': 7,
      'specialBusinessRole': 'synthetic-role',
    }, rare: false);
    expect(image.kind, EntityImageKind.normalGroup);
    expect(image.id, 7);
    expect(
      serviceGuestImage({'guestId': 7, 'runtimeGuestId': 27}, rare: true).id,
      7,
    );
  });

  test('未知身份不根据订单类型、名称、运行时ID或非法新字段猜图', () {
    for (final order in <Map<String, Object?>>[
      {'guestId': 7, 'guestName': '稀客七', 'runtimeGuestId': 27},
      {'guestId': 7, 'guestImageKind': 'rare-guest'},
      {'guestImageKind': 'rare-guest', 'guestImageId': -1},
      {'guestImageKind': 'rare-guest', 'guestImageId': '7'},
      {'guestImageKind': 'rare-guest', 'guestImageId': 7.0},
      {'guestId': 7, 'guestImageKind': 'unknown', 'guestImageId': 7},
    ]) {
      expect(serviceGuestImage(order, rare: false).id, isNull);
    }
    expect(
      serviceGuestImage({'guestId': 7, 'guestImageKind': ''}, rare: true).id,
      isNull,
    );
  });
}
