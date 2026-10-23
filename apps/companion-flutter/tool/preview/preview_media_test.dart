import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/resource_management.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'android_main.dart';
import 'rich_preview_workbench_model.dart';
import 'synthetic_game_media.dart';

/// 独立预览只使用人工绘制的内存图案；这些断言不验证真实游戏导出，也不连接宿主。
/// 保留解码、精确身份、门禁撤回与无Overlay恢复四条链路，避免预览用静态图片绕过产品行为。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUpAll(() async {
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null) {
      final bytes = await File(font).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  test('六类合成PNG均可解码为64px透明图案，不依赖打包资产或真实游戏', () async {
    final images = createSyntheticGameImages();
    expect(images, hasLength(39));
    final providers = images.values.toSet();
    expect(providers, hasLength(6));
    final distinctColors = <int>{};
    for (final provider in providers) {
      final codec = await ui.instantiateImageCodec(provider.bytes);
      try {
        final frame = await codec.getNextFrame();
        try {
          expect(frame.image.width, 64);
          expect(frame.image.height, 64);
          final rgba = (await frame.image.toByteData())!;
          var transparent = 0, colored = 0;
          for (var offset = 0; offset < rgba.lengthInBytes; offset += 4) {
            final alpha = rgba.getUint8(offset + 3);
            if (alpha == 0) transparent++;
            if (alpha > 0) {
              colored++;
              distinctColors.add(rgba.getUint32(offset));
            }
          }
          expect(transparent, greaterThan(0));
          expect(colored, greaterThan(0));
        } finally {
          frame.image.dispose();
        }
      } finally {
        codec.dispose();
      }
    }
    expect(distinctColors.length, greaterThanOrEqualTo(6));
  });

  test('预览图片使用显式命名空间与ID，配方双键保留重复材料及缺图回退', () {
    final model = RichPreviewWorkbenchModel();
    addTearDown(model.dispose);
    expect(model.gameMediaItems, hasLength(39));
    expect(model.gameMediaImage('food', 101), isA<MemoryImage>());
    expect(
      model.gameMediaImage('food', 1001),
      isNull,
      reason: '游戏配方ID不能代替料理ID',
    );
    expect(model.gameMediaImage('rare-guest', 301), isA<MemoryImage>());
    expect(
      model.gameMediaImage('rare-guest', 306),
      isNull,
      reason: '缺图条目继续使用产品旧图标',
    );
    expect(model.gameMediaImage('normal-group', 401), isA<MemoryImage>());
    expect(model.gameMediaImage('normal-group', 301), isNull);
    expect(model.gameMediaImage('food', -1), isNull);
    expect(model.gameMediaImage('unknown', 101), isNull);
    expect(model.gameMediaRecipes, hasLength(8));
    expect(model.gameMediaRecipes['108:1008']!['ingredientIds'], [2, 2, 3]);
    expect(model.gameMediaRecipes['108:1008']!['cookerType'], 4);
    expect(model.gameMediaRecipes['1008:108'], isNull);
    expect(
      model.gameMediaItems.every(
        (item) => '${item['name']}'.startsWith('合成图案 · '),
      ),
      isTrue,
    );
  });

  test('等待与失败立即撤回图片及配方并拒绝业务写入，恢复只更新内存代次', () async {
    var exits = 0;
    final model = RichPreviewWorkbenchModel(
      onExit: () async {
        exits++;
      },
    );
    addTearDown(model.dispose);
    for (final scenario in [
      'media-loading',
      'media-failed',
      'media-verifying',
      'offline',
    ]) {
      final previousRevision = model.gameMediaRevision;
      model.selectScenario(scenario);
      expect(model.gameMediaRevision, greaterThan(previousRevision));
      expect(model.gameMediaReady, isFalse);
      expect(model.gameMediaImage('food', 101), isNull);
      expect(model.gameMediaItems, isEmpty);
      expect(model.gameMediaRecipes, isEmpty);
      expect(model.canEdit, isFalse);
      expect(model.canExecute, isFalse);
      // 库存写入会替换 recommendationState，目录本身不会改变；必须比较实际
      // 库存投影，不能用“目录未变”这个即使错误写入也会通过的断言代替门禁验证。
      final runtime = model.runtime;
      await model.mutate(
        '/inventory/set',
        query: {'type': 'ingredient', 'id': '1', 'qty': '99'},
      );
      expect(model.runtime, runtime);
      expect(model.error, isNotEmpty);
      model.selectScenario('night');
      expect(model.gameMediaReady, isTrue);
      expect(model.gameMediaImage('food', 101), isNotNull);
    }
    model.selectScenario('media-failed');
    final beforeReload = model.gameMediaRevision;
    await model.reloadGameMedia();
    expect(model.gameMediaRevision, greaterThan(beforeReload));
    expect(model.gameMediaReady, isTrue);
    expect(model.scenario, 'night');
    model.selectScenario('viewer');
    final viewerRevision = model.gameMediaRevision;
    await model.reloadGameMedia();
    expect(model.gameMediaRevision, viewerRevision);
    expect(exits, 0);
  });

  for (final scenario in ['media-loading', 'media-failed']) {
    testWidgets('Android初始化门禁无Navigator/Overlay仍可结束：$scenario', (tester) async {
      const size = Size(390, 800);
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final model = RichPreviewWorkbenchModel(desktop: false)
        ..selectScenario(scenario);
      addTearDown(model.dispose);
      // 对应产品MaterialApp.builder门禁：有本地化与主题，但不挂载其Navigator child。
      // 不用测试专属Overlay让FSelect“碰巧可用”，直接验证真实预览按钮的恢复路径。
      await tester.pumpWidget(
        material.MaterialApp(
          builder: (context, child) => FTheme(
            data: stewardTheme(dark: false, touch: true),
            child: MediaQuery(
              data: const MediaQueryData(
                size: size,
                textScaler: TextScaler.linear(1.5),
              ),
              child: ListenableBuilder(
                listenable: model,
                builder: (context, child) => model.gameMediaReady
                    ? const Text('已恢复合成工作台')
                    : GameMediaInitialization(
                        model,
                        previewControls: AndroidPreviewControls(model),
                      ),
              ),
            ),
          ),
        ),
      );
      await tester.pump();
      expect(find.byType(Navigator), findsNothing);
      expect(find.byType(Overlay), findsNothing);
      expect(find.text('初始化游戏图像资源'), findsOneWidget);
      final exit = find.widgetWithText(StewardButton, '结束初始化预览');
      expect(tester.getRect(exit).right, lessThanOrEqualTo(size.width));
      expect(tester.getRect(exit).top, greaterThanOrEqualTo(0));
      await tester.tap(exit);
      // Forui 的按下/抬起反馈各有短计时器；等待真实反馈完成后再销毁门禁组件。
      await tester.pump(const Duration(milliseconds: 250));
      expect(model.scenario, 'night');
      expect(find.text('已恢复合成工作台'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }
}
