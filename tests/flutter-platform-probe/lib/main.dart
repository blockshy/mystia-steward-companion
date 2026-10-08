import 'dart:io' show Platform;
import 'dart:ui' show AppExitType;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'generated/probe_api.g.dart';
import 'probe_protocol.dart';
import 'install_fixture.dart';

void main(List<String> arguments) {
  if (arguments.contains('--updater-probe-mode=install-fixture')) {
    runApp(
      InstallFixtureApp(
        arguments: arguments,
        exchange: ProbeHostApi().exchange,
        autoInstall:
            Platform.environment['MYSTIA_UPDATER_PROBE_AUTOMATION'] ==
            'install-fixture-after-ready',
        close: () async {
          await ServicesBinding.instance.exitApplication(AppExitType.required);
        },
      ),
    );
    return;
  }
  runApp(
    ProbeApp(
      arguments: arguments,
      autoCancel:
          Platform.environment['MYSTIA_UPDATER_PROBE_AUTOMATION'] ==
          'cancel-after-ready',
      exchange: ProbeHostApi().exchange,
      close: () async {
        await ServicesBinding.instance.exitApplication(AppExitType.required);
      },
    ),
  );
}

class ProbeApp extends StatelessWidget {
  const ProbeApp({
    super.key,
    required this.arguments,
    required this.exchange,
    required this.close,
    this.autoCancel = false,
  });
  final List<String> arguments;
  final Future<String> Function(String command) exchange;
  final Future<void> Function() close;
  final bool autoCancel;

  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'mystia-steward-companion · 更新探针',
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      colorSchemeSeed: const Color(0xff775060),
      useMaterial3: true,
    ),
    home: ProbeScreen(
      arguments: arguments,
      exchange: exchange,
      close: close,
      autoCancel: autoCancel,
    ),
  );
}

enum ProbePhase { connecting, ready, cancelling, failed }

class ProbeScreen extends StatefulWidget {
  const ProbeScreen({
    super.key,
    required this.arguments,
    required this.exchange,
    required this.close,
    this.autoCancel = false,
  });
  final List<String> arguments;
  final Future<String> Function(String command) exchange;
  final Future<void> Function() close;
  final bool autoCancel;

  @override
  State<ProbeScreen> createState() => _ProbeScreenState();
}

class _ProbeScreenState extends State<ProbeScreen> {
  ProbePhase _phase = ProbePhase.connecting;
  String _detail = '正在校验与启动程序的连接…';
  ProbeLaunch? _launch;

  @override
  void initState() {
    super.initState();
    _connect();
  }

  Future<void> _connect() async {
    try {
      final launch = ProbeLaunch.parse(widget.arguments);
      _launch = launch;
      final response = await widget.exchange('hello');
      final reply = ProbeReply.parse(
        response,
        session: launch.session,
        requestId: 1,
      );
      if (!mounted) return;
      setState(() {
        _phase = ProbePhase.ready;
        _detail = reply.message;
      });
      if (widget.autoCancel) {
        // CI exercises the delivered UI and the same cancellation/engine-exit
        // path as the button. Wait for a rendered ready frame, not a timer.
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _cancel();
        });
      }
    } on Object catch (error) {
      _fail(error);
    }
  }

  Future<void> _cancel() async {
    if (_phase != ProbePhase.ready || _launch == null) return;
    setState(() {
      _phase = ProbePhase.cancelling;
      _detail = '正在结束探针并保存取消结果…';
    });
    try {
      final response = await widget.exchange('cancel');
      ProbeReply.parse(response, session: _launch!.session, requestId: 2);
      if (!mounted) return;
      await widget.close();
    } on Object catch (error) {
      _fail(error);
    }
  }

  void _fail(Object error) {
    if (!mounted) return;
    setState(() {
      _phase = ProbePhase.failed;
      _detail = '连接未通过校验。请保留报告并反馈此信息：\n$error';
    });
  }

  @override
  Widget build(BuildContext context) {
    final busy =
        _phase == ProbePhase.connecting || _phase == ProbePhase.cancelling;
    return Scaffold(
      body: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 680),
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(32),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text(
                    'mystia-steward-companion',
                    style: TextStyle(fontSize: 18),
                  ),
                  const SizedBox(height: 24),
                  Text(switch (_phase) {
                    ProbePhase.connecting => '连接启动程序',
                    ProbePhase.ready => '启动链已连通',
                    ProbePhase.cancelling => '正在结束探针',
                    ProbePhase.failed => '探针未通过',
                  }, style: Theme.of(context).textTheme.headlineMedium),
                  const SizedBox(height: 16),
                  const Text('这是迁移开发用的只读探针。此窗口用于验证 Flutter 资源加载、独立进程通信与取消退出。'),
                  const SizedBox(height: 24),
                  if (busy) const LinearProgressIndicator(),
                  const SizedBox(height: 16),
                  SelectableText(_detail),
                  const SizedBox(height: 24),
                  FilledButton(
                    autofocus: true,
                    onPressed: busy
                        ? null
                        : (_phase == ProbePhase.ready ? _cancel : widget.close),
                    child: Text(_phase == ProbePhase.failed ? '关闭窗口' : '结束探针'),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
