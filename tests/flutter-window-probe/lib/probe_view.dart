import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'probe_contract.dart';

const specimenSize = Size(320, 240);
const backgroundSample = Offset(48, 48);
const contentSample = Offset(160, 96);
const clearSample = Offset(32, 272);

class AlphaSpecimen extends StatelessWidget {
  const AlphaSpecimen({super.key, required this.spec});
  final PaintSpec spec;

  @override
  Widget build(BuildContext context) {
    spec.validate();
    return SizedBox.fromSize(
      size: specimenSize,
      child: Stack(
        children: [
          Positioned.fill(
            child: ColoredBox(
              color: Color(0xff000000 | probeBackgroundRgb)
                  .withAlpha(spec.backgroundAlpha),
            ),
          ),
          Positioned.fill(
            child: Opacity(
              opacity: spec.contentAlpha / 255,
              child: const Stack(
                children: [
                  Positioned(
                    left: 112,
                    top: 64,
                    width: 96,
                    height: 64,
                    child: ColoredBox(color: Color(0xffe04020)),
                  ),
                  Positioned(
                    left: 24,
                    top: 168,
                    child: Text(
                      'Aa 0123 透明度测试',
                      style: TextStyle(color: Color(0xffe04020), fontSize: 24),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class ProbeUiController extends ChangeNotifier {
  PaintSpec paint = const PaintSpec(1, 255, 255);
  String detail = '正在核验原生窗口与受控底窗';
  int pointerCount = 0;
  int keyCount = 0;
  bool focused = false;

  void setPaint(PaintSpec value) {
    value.validate();
    if (value.revision <= paint.revision) {
      throw StateError('Paint revisions must increase.');
    }
    paint = value;
    notifyListeners();
  }

  void setDetail(String value) {
    detail = value;
    notifyListeners();
  }

  void pointerDown() {
    pointerCount++;
    notifyListeners();
  }

  void keyDown() {
    keyCount++;
    notifyListeners();
  }

  void focusChanged(bool value) {
    focused = value;
    notifyListeners();
  }
}

class WindowProbeApp extends StatelessWidget {
  const WindowProbeApp({
    super.key,
    required this.controller,
    required this.restore,
  });
  final ProbeUiController controller;
  final Future<void> Function() restore;

  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'mystia-steward-companion · 窗口能力探针',
    debugShowCheckedModeBanner: false,
    theme: ThemeData(useMaterial3: true, canvasColor: Colors.transparent),
    home: ProbeSurface(controller: controller, restore: restore),
  );
}

class ProbeSurface extends StatefulWidget {
  const ProbeSurface({
    super.key,
    required this.controller,
    required this.restore,
  });
  final ProbeUiController controller;
  final Future<void> Function() restore;
  @override
  State<ProbeSurface> createState() => _ProbeSurfaceState();
}

class _ProbeSurfaceState extends State<ProbeSurface> {
  bool _restoring = false;
  String? _error;

  Future<void> _restore() async {
    if (_restoring) return;
    setState(() {
      _restoring = true;
      _error = null;
    });
    try {
      await widget.restore();
    } on Object catch (error) {
      if (mounted) setState(() => _error = error.toString());
    } finally {
      if (mounted) setState(() => _restoring = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    backgroundColor: Colors.transparent,
    body: Focus(
      autofocus: true,
      onFocusChange: widget.controller.focusChanged,
      onKeyEvent: (_, event) {
        if (event is KeyDownEvent &&
            event.logicalKey == LogicalKeyboardKey.f24) {
          widget.controller.keyDown();
          return KeyEventResult.handled;
        }
        return KeyEventResult.ignored;
      },
      child: Listener(
        behavior: HitTestBehavior.translucent,
        onPointerDown: (_) => widget.controller.pointerDown(),
        child: ListenableBuilder(
          listenable: widget.controller,
          builder: (context, _) => Stack(
            children: [
              Positioned(
                left: 0,
                top: 0,
                child: AlphaSpecimen(spec: widget.controller.paint),
              ),
              Positioned(
                left: 352,
                right: 16,
                top: 16,
                bottom: 16,
                child: ColoredBox(
                  color: const Color(0xfff2f2f2),
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: SingleChildScrollView(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          const Text(
                            '独立窗口能力探针',
                            style: TextStyle(fontSize: 22),
                          ),
                          const SizedBox(height: 16),
                          Text(widget.controller.detail),
                          const SizedBox(height: 16),
                          Text(
                            '背景 alpha：${widget.controller.paint.backgroundAlpha}/255',
                          ),
                          Text(
                            '内容 alpha：${widget.controller.paint.contentAlpha}/255',
                          ),
                          Text(
                            '绘制 revision：${widget.controller.paint.revision}',
                          ),
                          Text(
                            'Flutter 指针：${widget.controller.pointerCount}；焦点探针 F24：${widget.controller.keyCount}',
                          ),
                          Text('Flutter 焦点：${widget.controller.focused}'),
                          const SizedBox(height: 16),
                          FilledButton(
                            onPressed: _restoring ? null : _restore,
                            child: Text(_restoring ? '等待原生状态回读' : '恢复可操作窗口'),
                          ),
                          if (_error != null)
                            Text(_error!, key: const ValueKey('restore-error')),
                          const SizedBox(height: 16),
                          const Text('自动检查只作用于本探针及受控底窗。此结果不代表整个 P0 通过。'),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    ),
  );
}
