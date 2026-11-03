// Template for lib/firebase_options.dart (the real file is gitignored).
//
// Option A - no Firebase (default; the wallet works with JWT only):
//   cp firebase_options.template.dart lib/firebase_options.dart
//
// Option B - your own Firebase project:
//   dart pub global activate flutterfire_cli
//   flutterfire configure --project=<your-firebase-project-id>
// FlutterFire overwrites lib/firebase_options.dart and writes
// android/app/google-services.json and ios/Runner/GoogleService-Info.plist.
// All three are gitignored; never commit them.
//
// ignore_for_file: type=lint
import 'package:firebase_core/firebase_core.dart' show FirebaseOptions;

/// Placeholder options. [currentPlatform] throws, so `initClearPayFirebase`
/// logs "Firebase skipped" and the app continues with JWT login against the
/// ASP.NET API. Money never depends on Firebase.
class DefaultFirebaseOptions {
  static FirebaseOptions get currentPlatform {
    throw UnsupportedError(
      'Firebase is not configured. Run `flutterfire configure` to generate '
      'lib/firebase_options.dart for your own Firebase project.',
    );
  }
}
