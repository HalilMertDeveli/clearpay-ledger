import 'dart:async';

import 'package:signalr_netcore/http_connection_options.dart';
import 'package:signalr_netcore/hub_connection.dart';
import 'package:signalr_netcore/hub_connection_builder.dart';

import '../platform/host.dart';

/// T-071: refresh hint. Android uses `/hubs/wallet`. Other hosts poll JWT wallet (T-098).
class WalletLiveHub {
  HubConnection? _connection;
  Timer? _poll;

  Future<void> connect({
    required String baseUrl,
    required String? Function() token,
    required void Function() onChanged,
    Duration pollEvery = const Duration(seconds: 8),
  }) async {
    await dispose();
    if (!isAndroidHost) {
      _poll = Timer.periodic(pollEvery, (_) => onChanged());
      return;
    }

    final root = baseUrl.endsWith('/') ? baseUrl.substring(0, baseUrl.length - 1) : baseUrl;
    final options = HttpConnectionOptions(
      accessTokenFactory: () async => token() ?? '',
      requestTimeout: 15000,
    );
    final connection = HubConnectionBuilder()
        .withUrl('$root/hubs/wallet', options: options)
        .withAutomaticReconnect()
        .build();
    connection.on('WalletChanged', (_) {
      onChanged();
    });
    _connection = connection;
    try {
      await connection.start();
    } catch (_) {
      await dispose();
    }
  }

  Future<void> dispose() async {
    _poll?.cancel();
    _poll = null;
    final connection = _connection;
    _connection = null;
    if (connection == null) {
      return;
    }
    try {
      await connection.stop();
    } catch (_) {
      /* already down */
    }
  }
}
