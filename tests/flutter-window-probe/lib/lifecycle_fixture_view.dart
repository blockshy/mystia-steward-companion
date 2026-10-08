import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'generated/window_api.g.dart';

typedef PublishLifecycleUi = Future<int> Function(LifecycleUiEvidence evidence);

class LifecycleFixtureApp extends StatefulWidget {
  const LifecycleFixtureApp({
    super.key,
    required this.gitSha,
    required this.publish,
    required this.onFailure,
  });

  final String gitSha;
  final PublishLifecycleUi publish;
  final void Function(Object error) onFailure;

  @override
  State<LifecycleFixtureApp> createState() => _LifecycleFixtureAppState();
}

class _LifecycleFixtureAppState extends State<LifecycleFixtureApp> {
  bool _ready = false;
  bool _focused = false;
  bool _failed = false;
  int _pointerDown = 0;
  int _keyDown = 0;
  int _sequence = 0;
  Future<void> _publication = Future<void>.value();

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      _ready = true;
      _publish();
    });
  }

  void _publish() {
    if (!_ready || _failed) return;
    final evidence = LifecycleUiEvidence(
      gitSha: widget.gitSha,
      ready: _ready,
      pointerDown: _pointerDown,
      keyDown: _keyDown,
      focused: _focused,
      sequence: ++_sequence,
    );
    _publication = _publication
        .then((_) async {
          if (_failed || !mounted) return;
          final acknowledged = await widget
              .publish(evidence)
              .timeout(const Duration(seconds: 5));
          if (acknowledged != evidence.sequence) {
            throw StateError('Native UI evidence acknowledgement differs.');
          }
        })
        .catchError((Object error) {
          if (_failed || !mounted) return;
          _failed = true;
          widget.onFailure(error);
        });
  }

  @override
  Widget build(BuildContext context) => MaterialApp(
    debugShowCheckedModeBanner: false,
    home: Scaffold(
      backgroundColor: const Color(0xff2040c0),
      body: Focus(
        autofocus: true,
        onFocusChange: (focused) {
          _focused = focused;
          _publish();
        },
        onKeyEvent: (_, event) {
          if (event is KeyDownEvent &&
              event.logicalKey == LogicalKeyboardKey.f24) {
            setState(() => _keyDown++);
            _publish();
            return KeyEventResult.handled;
          }
          return KeyEventResult.ignored;
        },
        child: Listener(
          behavior: HitTestBehavior.opaque,
          onPointerDown: (_) {
            setState(() => _pointerDown++);
            _publish();
          },
          child: SizedBox.expand(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Text(
                '生命周期受控主实例\n指针：$_pointerDown；焦点键 F24：$_keyDown',
                style: const TextStyle(color: Colors.white, fontSize: 22),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}
