import 'dart:async';
import 'dart:convert';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/entity_image.dart';
import 'package:mystia_steward_companion/ui/game_media_scope.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 使用内存 PNG 模拟已完成认证与校验的资源代次；组件测试不读取游戏、不联网，
/// 不依赖随应用打包的旧图片目录。50px 方形原图便于验证显示槽位独立于图片像素。
MemoryImage _image({bool corrupt = false}) => MemoryImage(
  base64Decode(
    corrupt ? 'AAAA' : 'iVBORw0KGgoAAAANSUhEUgAAADIAAAAyCAYAAAAeP4ixAAAASElEQVR42u3PMQ0AMAgAsGFjL/4tYQU08JLWQSN/9TsgREREREREREREREREREREREREREREREREREREREREREREREREREQ2BgNuaUcSjuqqAAAAAElFTkSuQmCC',
  ),
);

Widget _fixture(
  Widget child, {
  bool touch = false,
  double scale = 1,
  ImageProvider<Object>? Function(String, int)? resolveImage,
  Object? revision,
}) {
  final theme = stewardTheme(
    dark: false,
    touch: touch,
    backgroundOpacity: .25,
    contentOpacity: .6,
  );
  return material.MaterialApp(
    theme: theme.toApproximateMaterialTheme(),
    home: FTheme(
      data: theme,
      child: MediaQuery(
        data: MediaQueryData(textScaler: TextScaler.linear(scale)),
        child: GameMediaScope(
          resolveImage: resolveImage ?? (namespace, id) => null,
          revision: revision,
          child: Center(child: child),
        ),
      ),
    ),
  );
}

/// codec 由真实异步区完成；不使用延时或 fake-async 的 settle 等待平台图片解码。
Future<void> _decode(
  WidgetTester tester,
  Image image, {
  bool fails = false,
}) async {
  await tester.runAsync(() async {
    final done = Completer<void>();
    final stream = image.image.resolve(ImageConfiguration.empty);
    late ImageStreamListener listener;
    listener = ImageStreamListener(
      (info, synchronous) {
        if (done.isCompleted) return;
        if (fails) {
          done.completeError(StateError('损坏图片意外解码成功'));
        } else {
          done.complete();
        }
      },
      onError: (Object error, StackTrace? stack) {
        if (done.isCompleted) return;
        if (fails) {
          done.complete();
        } else {
          done.completeError(error, stack);
        }
      },
    );
    stream.addListener(listener);
    try {
      await done.future;
    } finally {
      stream.removeListener(listener);
    }
  });
  await tester.pump();
}

void main() {
  test('图片身份只接受非负整数，客群与稀客保持独立命名空间', () {
    expect(entityImageId(0), 0);
    for (final value in [null, -1, '42', 42.0, '蘑菇']) {
      expect(entityImageId(value), isNull);
    }
    expect(EntityImageKind.rareGuest.namespace, 'rare-guest');
    expect(EntityImageKind.normalGroup.namespace, 'normal-group');
  });

  testWidgets('非法身份不查询资源，合法身份传递精确命名空间且无Scope安全回退', (tester) async {
    final requested = <(String, int)>[];
    await tester.pumpWidget(
      _fixture(
        const Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            EntityImage(
              kind: EntityImageKind.food,
              id: null,
              fallback: FLucideIcons.utensils,
            ),
            EntityImage(
              kind: EntityImageKind.food,
              id: -1,
              fallback: FLucideIcons.utensils,
            ),
            EntityImage(
              kind: EntityImageKind.beverage,
              id: 42,
              fallback: FLucideIcons.glassWater,
            ),
            EntityImage(
              kind: EntityImageKind.normalGroup,
              id: 42,
              fallback: FLucideIcons.users,
            ),
          ],
        ),
        resolveImage: (namespace, id) {
          requested.add((namespace, id));
          return null;
        },
      ),
    );
    expect(requested, [('beverage', 42), ('normal-group', 42)]);
    expect(find.byType(Image), findsNothing);
    await tester.pumpWidget(
      const material.MaterialApp(
        home: Center(
          child: EntityImage(
            kind: EntityImageKind.food,
            id: 42,
            fallback: FLucideIcons.utensils,
          ),
        ),
      ),
    );
    expect(find.byIcon(FLucideIcons.utensils), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  for (final touch in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('标签20/名称28随字号缩放且前景因子只应用一次 touch=$touch scale=$scale', (
        tester,
      ) async {
        final provider = _image();
        await tester.pumpWidget(
          _fixture(
            const Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                EntityImage(
                  kind: EntityImageKind.food,
                  id: 42,
                  fallback: FLucideIcons.utensils,
                  key: ValueKey('tag-image'),
                ),
                EntityName(
                  '合成料理',
                  kind: EntityImageKind.food,
                  id: 42,
                  fallback: FLucideIcons.utensils,
                ),
              ],
            ),
            touch: touch,
            scale: scale,
            resolveImage: (namespace, id) => provider,
          ),
        );
        final tag = find.byKey(const ValueKey('tag-image'));
        final name = find.descendant(
          of: find.byType(EntityName),
          matching: find.byType(EntityImage),
        );
        expect(tester.getSize(tag), Size(20 * scale, 20 * scale));
        expect(tester.getSize(name), Size(28 * scale, 28 * scale));
        // widgetList 为依赖当前 Element 树的惰性集合；解码期间 pump 会重建图片，
        // 必须先保存 Widget 快照，避免下一次迭代访问已经卸载的旧 Element。
        final images = tester.widgetList<Image>(find.byType(Image)).toList();
        for (final image in images) {
          expect(image.opacity!.value, .6);
          expect(image.excludeFromSemantics, isTrue);
          await _decode(tester, image);
        }
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox.shrink());
      });
    }
  }

  testWidgets('缺失与损坏资源保留原图标，窄屏150%标签等高且读屏不重复', (tester) async {
    final semantics = tester.ensureSemantics();
    try {
      final provider = _image(corrupt: true);
      await tester.pumpWidget(
        _fixture(
          const SizedBox(
            width: 262,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                OrderTag(
                  '相同材料',
                  kind: OrderTagKind.ingredient,
                  key: ValueKey('old-tag'),
                ),
                OrderTag(
                  '相同材料',
                  kind: OrderTagKind.ingredient,
                  key: ValueKey('missing-tag'),
                  leading: EntityImage(
                    kind: EntityImageKind.ingredient,
                    id: null,
                    fallback: FLucideIcons.leaf,
                  ),
                ),
                EntityImage(
                  kind: EntityImageKind.food,
                  id: 42,
                  fallback: FLucideIcons.utensils,
                ),
              ],
            ),
          ),
          scale: 1.5,
          resolveImage: (namespace, id) => provider,
        ),
      );
      await _decode(
        tester,
        tester.widget<Image>(find.byType(Image)),
        fails: true,
      );
      expect(find.byIcon(FLucideIcons.utensils), findsOneWidget);
      expect(
        tester.getSize(find.byKey(const ValueKey('old-tag'))),
        tester.getSize(find.byKey(const ValueKey('missing-tag'))),
      );
      expect(find.bySemanticsLabel('基础食材：相同材料'), findsNWidgets(2));
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    } finally {
      semantics.dispose();
    }
  });

  testWidgets('当前代次撤回后不保留旧图，回退过程不改变名称布局', (tester) async {
    final provider = _image();
    const child = EntityName(
      '合成料理',
      kind: EntityImageKind.food,
      id: 42,
      fallback: FLucideIcons.utensils,
    );
    await tester.pumpWidget(
      _fixture(child, revision: 1, resolveImage: (namespace, id) => provider),
    );
    await _decode(tester, tester.widget<Image>(find.byType(Image)));
    final before = tester.getRect(find.text('合成料理'));
    await tester.pumpWidget(_fixture(child, revision: 2));
    expect(find.byType(Image), findsNothing);
    expect(find.byIcon(FLucideIcons.utensils), findsOneWidget);
    expect(tester.getRect(find.text('合成料理')), before);
    expect(tester.takeException(), isNull);
  });
}
