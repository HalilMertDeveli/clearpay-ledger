# ClearPay mobile (Flutter)

Flutter client for the ClearPay demo wallet. It talks to the same ASP.NET Core host as the
web UI, using JWT bearer tokens. It keeps **no local balance**: every number on screen comes
from `GET /api/wallet`, which derives the balance from the SQL Server ledger.

> Portfolio demo. Not a licensed payment or e-money product. The bank gateway is simulated.

## Operations

| Operation | API |
|-----------|-----|
| Sign in | `POST /api/token` (or `POST /api/token/firebase` when Firebase Auth is configured) |
| Register | `POST /api/register` |
| Forgot / reset password | `POST /api/password/forgot`, `POST /api/password/reset` |
| Overview | `GET /api/wallet`, live refresh hint via SignalR `/hubs/wallet` |
| Transfer | `POST /api/transfers` with an `Idempotency-Key` (GUID per confirmed send) -> 201 / 409 |
| Top-up / withdraw | `POST /api/topup`, `POST /api/withdraw` (simulated bank gateway) |
| Movements | `GET /api/movements` |
| Receipt | `GET /api/receipts/{correlationId}`, `GET /api/receipts/{correlationId}/pdf` |
| Admin (role) | `/api/admin/*` |

## Run

Start the API first (see the root `README.md`), then:

```bash
cd mobile/clearpay
cp firebase_options.template.dart lib/firebase_options.dart   # JWT-only mode, no Firebase
flutter pub get
flutter run                                   # Android emulator uses http://10.0.2.2:5153
flutter run --dart-define=CLEARPAY_API=http://localhost:5153   # override the API base URL
```

## Optional: Firebase

Firebase is optional. Money never depends on it; it only provides an alternative sign-in and a
health ping document (`app_meta/ping`, rules in `firestore.rules`). To use your own project:

```bash
dart pub global activate flutterfire_cli
flutterfire configure --project=<your-firebase-project-id>
```

This generates `lib/firebase_options.dart`, `android/app/google-services.json` and
`ios/Runner/GoogleService-Info.plist`. All three are gitignored; never commit them. Configure
the API with `Firebase:ProjectId` (user-secrets or environment variable) so it can verify
Firebase ID tokens.

## Test

```bash
flutter analyze
flutter test
```

The widget tests use a mocked HTTP client; they need neither the API nor Firebase.
