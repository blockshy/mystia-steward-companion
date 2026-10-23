import 'package:companion_platform/companion_platform.dart';
import 'package:flutter/widgets.dart';

/// 真实Windows逐像素合成的人工验收入口，不连接业务API或加载生产身份。
///
/// 将它编入独立temp runner，在黑/白/绿色背景板前检查：四列应分别为透明、
/// 25%红、50%红和纯红；白底文字保持不透明。仅检查原生API返回值不足以证明效果。
/// 运行必须传入隔离参数；客户区透明的空白会按Win32逐像素命中规则透过鼠标。
Future<void> main(List<String> arguments) async {
  WidgetsFlutterBinding.ensureInitialized();
  if (!arguments.any((arg) => arg.startsWith('--isolation-id=opacity-')) ||
      !arguments.any(
        (arg) =>
            arg.startsWith('--control-port=') && arg != '--control-port=32146',
      ) ||
      arguments.any(
        (arg) => arg.startsWith('--game-pid=') || arg.startsWith('--token='),
      )) {
    throw StateError('逐像素合成验收只允许无游戏、无Token的隔离实例。');
  }
  final platform = CompanionPlatform.instance;
  final launch = await platform.initialize();
  if (!launch.desktop || launch.gamePid != null || launch.token != null) {
    throw StateError('逐像素合成验收的原生启动信息不符合隔离范围。');
  }
  runApp(
    Directionality(
      textDirection: TextDirection.ltr,
      child: Column(
        children: [
          const ColoredBox(
            color: Color(0xFFFFFFFF),
            child: Text(
              '原生透明度回归：背景板应穿过前三列；第四列与文字应不透明。',
              style: TextStyle(color: Color(0xFF000000), fontSize: 20),
            ),
          ),
          Expanded(
            child: Row(
              children: [
                for (final alpha in [0, 64, 128, 255])
                  Expanded(
                    child: ColoredBox(
                      color: Color.fromARGB(alpha, 255, 0, 0),
                      child: Align(
                        alignment: Alignment.topCenter,
                        child: ColoredBox(
                          color: const Color(0xFFFFFFFF),
                          child: Text(
                            'alpha=$alpha',
                            style: const TextStyle(
                              color: Color(0xFF000000),
                              fontSize: 18,
                            ),
                          ),
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
          GestureDetector(
            onTap: platform.exit,
            child: const ColoredBox(
              color: Color(0xFF222222),
              child: Padding(
                padding: EdgeInsets.all(16),
                child: Text(
                  '退出本次隔离验收',
                  style: TextStyle(color: Color(0xFFFFFFFF), fontSize: 18),
                ),
              ),
            ),
          ),
        ],
      ),
    ),
  );
}
