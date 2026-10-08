import 'dart:convert';

import 'package:flutter/material.dart';

import 'probe_protocol.dart';

class InstallReply {
  InstallReply(
    this.state,
    this.message,
    this.sequence,
    this.progress,
    this.terminal,
    this.canCancel,
  );
  final String state, message;
  final int sequence, progress;
  final bool terminal, canCancel;
  factory InstallReply.parse(
    String text,
    String session,
    int request,
    int previous,
  ) {
    if (utf8.encode(text).length > 16384) {
      throw const FormatException('安装响应过大。');
    }
    final value = jsonDecode(text);
    const keys = {
      'protocolVersion',
      'session',
      'requestId',
      'stateSequence',
      'state',
      'message',
      'progress',
      'terminal',
      'canCancel',
    };
    const states = {
      'ready',
      'waiting',
      'preparing',
      'waiting-game',
      'game-closed',
      'backing-up',
      'installing',
      'verifying',
      'succeeded',
      'failed',
      'cancelled',
    };
    if (value is! Map<String, dynamic> ||
        value.length != keys.length ||
        !value.keys.every(keys.contains) ||
        value['protocolVersion'] != 2 ||
        value['protocolVersion'] is! int ||
        value['session'] != session ||
        value['requestId'] != request ||
        value['requestId'] is! int ||
        value['stateSequence'] is! int ||
        (value['stateSequence'] as int) < previous ||
        (value['stateSequence'] as int) < 1 ||
        !states.contains(value['state']) ||
        value['message'] is! String ||
        (value['message'] as String).isEmpty ||
        value['progress'] is! int ||
        (value['progress'] as int) < 0 ||
        (value['progress'] as int) > 100 ||
        value['terminal'] is! bool ||
        value['canCancel'] is! bool ||
        (request == 1 && value['state'] != 'ready')) {
      throw const FormatException('安装响应与当前 fixture 会话不匹配。');
    }
    final terminal = {
      'succeeded',
      'failed',
      'cancelled',
    }.contains(value['state']);
    if (terminal != value['terminal'] ||
        (terminal && value['canCancel'] == true) ||
        (value['state'] == 'succeeded' && value['progress'] != 100)) {
      throw const FormatException('安装终态不一致。');
    }
    return InstallReply(
      value['state'] as String,
      value['message'] as String,
      value['stateSequence'] as int,
      value['progress'] as int,
      terminal,
      value['canCancel'] as bool,
    );
  }
}

class InstallFixtureApp extends StatefulWidget {
  const InstallFixtureApp({
    super.key,
    required this.arguments,
    required this.exchange,
    required this.close,
    this.autoInstall = false,
  });
  final List<String> arguments;
  final Future<String> Function(String) exchange;
  final Future<void> Function() close;
  final bool autoInstall;
  @override
  State<InstallFixtureApp> createState() => _InstallFixtureAppState();
}

class _InstallFixtureAppState extends State<InstallFixtureApp> {
  ProbeLaunch? _launch;
  InstallReply? _reply;
  int _request = 0, _sequence = 0;
  bool _busy = true, _started = false, _cancelRequested = false;
  String? _error;
  @override
  void initState() {
    super.initState();
    _connect();
  }

  Future<void> _send(String command) async {
    final request = ++_request;
    final raw = await widget.exchange(command);
    final reply = InstallReply.parse(raw, _launch!.session, request, _sequence);
    _sequence = reply.sequence;
    if (mounted) setState(() => _reply = reply);
  }

  Future<void> _connect() async {
    try {
      _launch = ProbeLaunch.parse(widget.arguments);
      if (!_launch!.installFixture) throw const FormatException('缺少明确的隔离安装模式。');
      await _send('hello');
      if (!mounted) return;
      setState(() => _busy = false);
      if (widget.autoInstall) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _start();
        });
      }
    } on Object catch (error) {
      _fail(error);
    }
  }

  Future<void> _start() async {
    if (_started || _busy || _reply?.state != 'ready') return;
    setState(() {
      _started = true;
      _busy = true;
    });
    try {
      await _send('start');
      while (mounted && !_reply!.terminal) {
        // This is a polling cadence; only a validated core reply establishes state.
        await Future<void>.delayed(const Duration(milliseconds: 100));
        final command = _cancelRequested ? 'cancel' : 'status';
        _cancelRequested = false;
        await _send(command);
      }
      if (!mounted) return;
      setState(() => _busy = false);
      if (widget.autoInstall) await _finish();
    } on Object catch (error) {
      _fail(error);
    }
  }

  Future<void> _cancel() async {
    if (_reply?.canCancel != true) return;
    if (_started) {
      setState(() => _cancelRequested = true);
      return;
    }
    setState(() => _busy = true);
    try {
      await _send('cancel');
      if (mounted) setState(() => _busy = false);
    } on Object catch (error) {
      _fail(error);
    }
  }

  Future<void> _finish() async {
    if (_reply?.terminal != true || _busy) return;
    setState(() => _busy = true);
    try {
      await _send('finish');
      await widget.close();
    } on Object catch (error) {
      _fail(error);
    }
  }

  void _fail(Object error) {
    if (mounted) {
      setState(() {
        _error = error.toString();
        _busy = false;
      });
    }
  }

  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'mystia-steward-companion · 隔离安装验证',
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      colorSchemeSeed: const Color(0xff775060),
      useMaterial3: true,
    ),
    home: Scaffold(
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
                    '完整 Flutter bundle · 隔离安装验证',
                    style: TextStyle(fontSize: 26),
                  ),
                  const SizedBox(height: 20),
                  const Text('仅修改本次新建的临时 fixture。不会修改真实游戏、关闭游戏或访问正式更新服务。'),
                  const SizedBox(height: 20),
                  if (_busy)
                    LinearProgressIndicator(
                      value: _reply == null ? null : _reply!.progress / 100,
                    ),
                  const SizedBox(height: 20),
                  SelectableText(_error ?? _reply?.message ?? '正在验证安装会话…'),
                  const SizedBox(height: 24),
                  Wrap(
                    spacing: 16,
                    runSpacing: 12,
                    children: [
                      if (!_started && _error == null)
                        FilledButton(
                          onPressed: _busy ? null : _start,
                          child: const Text('开始隔离安装'),
                        ),
                      if (_reply?.canCancel == true && _error == null)
                        OutlinedButton(
                          onPressed: _cancelRequested ? null : _cancel,
                          child: const Text('取消安装'),
                        ),
                      if (_reply?.terminal == true && _error == null)
                        FilledButton(
                          onPressed: _busy ? null : _finish,
                          child: const Text('完成并退出'),
                        ),
                      if (_error != null)
                        FilledButton(
                          onPressed: widget.close,
                          child: const Text('关闭窗口'),
                        ),
                    ],
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    ),
  );
}
