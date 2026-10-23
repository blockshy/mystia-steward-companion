import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/extension_item_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/text_geometry.dart';

/// 按实际平台正文宽度验证邀请信息密度：Windows640扣除80导航和两侧24，
/// Android390扣除两侧16。使用真实标准控件和中文字体，不缩小字号或按钮命中区。
/// 此处只验证展示回调；场景变化、唯一ID与权限检查继续由真实ToolsPage专项覆盖。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final font = FontLoader('Microsoft YaHei')
        ..addFont(
          Future.value(ByteData.sublistView(await File(path).readAsBytes())),
        );
      await font.load();
    }
  });

  for (final desktop in [true, false]) {
    for (final scale in [1.0, 1.5]) {
      for (final dark in [false, true]) {
        testWidgets('邀请卡紧凑完整且真实按钮可达 desktop=$desktop scale=$scale dark=$dark', (
          tester,
        ) async {
          final width = desktop ? 640.0 : 390.0;
          tester.view.physicalSize = Size(width, 800);
          tester.view.devicePixelRatio = 1;
          addTearDown(tester.view.resetPhysicalSize);
          addTearDown(tester.view.resetDevicePixelRatio);
          final theme = stewardTheme(dark: dark, touch: !desktop);
          var invites = 0;
          await tester.pumpWidget(
            material.MaterialApp(
              localizationsDelegates: FLocalizations.localizationsDelegates,
              supportedLocales: FLocalizations.supportedLocales,
              theme: theme.toApproximateMaterialTheme(),
              builder: (_, child) => FTheme(
                data: theme,
                child: MediaQuery(
                  data: MediaQueryData(
                    size: Size(width, 800),
                    textScaler: TextScaler.linear(scale),
                  ),
                  child: child!,
                ),
              ),
              home: Padding(
                padding: desktop
                    ? const EdgeInsets.fromLTRB(104, 16, 24, 16)
                    : const EdgeInsets.all(16),
                child: ListView(
                  children: [
                    for (var index = 0; index < 3; index++)
                      InvitationItemContent(
                        {
                          'id': 301 + index,
                          'name': '合成稀客',
                          'status': 'available',
                          'kizunaLevel': 3,
                          'locationStatus': 'current-map',
                          'sceneNames': ['合成地图'],
                          'reason': '满足羁绊条件，可邀请。',
                          'canInvite': true,
                        },
                        key: ValueKey('candidate-$index'),
                        onInvite: index == 0 ? () => invites++ : null,
                      ),
                  ],
                ),
              ),
            ),
          );
          await tester.pumpAndSettle();
          final first = find.byKey(const ValueKey('candidate-0'));
          Finder inside(Finder finder) =>
              find.descendant(of: first, matching: finder);
          final card = tester.getRect(first);
          final heading = tester.getRect(
            inside(find.byKey(const ValueKey('invitation-heading-row'))),
          );
          final action = inside(find.byType(StewardButton));
          final button = tester.getRect(action);
          final name = tester.getRect(inside(find.text('合成稀客')));
          expect(card.width, desktop ? 512 : 358);
          // 此上限约束普通完整卡片的实际信息密度，而不是只验证无overflow；
          // 150%仍留出随字体增长的空间，不能靠删原因或压缩触屏按钮满足上限。
          expect(card.height, lessThanOrEqualTo(scale == 1 ? 165 : 215));
          expect(name.right, lessThan(button.left));
          expect(button.center.dy, closeTo(heading.center.dy, .01));
          expect(button.height, greaterThanOrEqualTo(desktop ? 36 : 44));
          expect(button.right, lessThanOrEqualTo(card.right - 10));
          expect(
            textBaseline(tester, inside(find.text('可邀请'))),
            closeTo(textBaseline(tester, inside(find.text('3 级'))), .1),
          );
          expect(inside(find.text('合成地图')), findsOneWidget);
          expect(inside(find.text('当前地图已登记')), findsOneWidget);
          expect(inside(find.text('满足羁绊条件，可邀请。')), findsOneWidget);
          final image = tester.widget<EntityImage>(
            inside(find.byType(EntityImage)),
          );
          expect(image.kind, EntityImageKind.rareGuest);
          expect(image.id, 301);
          expect(
            tester.getSize(inside(find.byType(EntityImage))).width,
            closeTo(28 * scale, .01),
          );
          expect(
            tester.getRect(find.byKey(const ValueKey('candidate-1'))).bottom,
            lessThan(784),
            reason: '普通完整资料至少两张能同时进入初始视口，不再被固定文档留白挤占',
          );
          expect(action.hitTestable(), findsOneWidget);
          await tester.tap(action);
          await tester.pumpAndSettle();
          expect(invites, 1);
          final disabled = find.descendant(
            of: find.byKey(const ValueKey('candidate-1')),
            matching: find.byType(StewardButton),
          );
          expect(
            tester.widget<StewardButton>(disabled).onPressed,
            isNull,
            reason: '只改变布局，不从status=available推导或恢复父级未授予的邀请权限',
          );
          expect(tester.takeException(), isNull);
          await tester.pumpWidget(const SizedBox());
        });
      }
    }
  }
}
