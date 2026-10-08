import 'package:pigeon/pigeon.dart';

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/generated/storage_probe_api.g.dart',
    kotlinOut: 'android/app/src/main/kotlin/com/tyukki/mystia/steward/companion/storagep0/StorageProbeApi.g.kt',
    kotlinOptions: KotlinOptions(
      package: 'com.tyukki.mystia.steward.companion.storagep0',
    ),
  ),
)
@HostApi()
abstract class StorageProbeApi {
  String launchConfiguration();
  void publishReport(String report);
}
