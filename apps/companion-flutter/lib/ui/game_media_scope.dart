import 'package:flutter/widgets.dart';

/// 组件只同步取得已验证的图片 Provider，不拥有网络、文件或连接凭据。
/// revision 覆盖图片下载及资源代次变化，避免相同 ID 在换游戏电脑后沿用旧图。
/// 测试可直接注入 MemoryImage，不读取游戏资源或打包静态图片。
class GameMediaScope extends InheritedWidget {
  const GameMediaScope({
    required this.resolveImage,
    this.revision,
    required super.child,
    super.key,
  });
  final ImageProvider<Object>? Function(String namespace, int id) resolveImage;
  final Object? revision;

  static ImageProvider<Object>? imageFor(
    BuildContext context,
    String namespace,
    int id,
  ) => context
      .dependOnInheritedWidgetOfExactType<GameMediaScope>()
      ?.resolveImage(namespace, id);

  @override
  bool updateShouldNotify(GameMediaScope oldWidget) =>
      revision != oldWidget.revision || resolveImage != oldWidget.resolveImage;
}
