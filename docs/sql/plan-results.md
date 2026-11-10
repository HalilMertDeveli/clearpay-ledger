# Execution plans: before / after

Measured 2026-11-09 with `scripts/sql/Measure-LedgerPlans.ps1` on Microsoft SQL Server 2022 (RTM-CU27-GDR) (KB5126934) - 16.0.4305.5 (X64).
Data: 1000000 ledger rows (100000 on the measured wallet), 125000 payment operations (synthetic).
Warm cache; median of 5 runs; numbers from the actual plans. `.sqlplan` files are in this folder.

| Query | Phase | Rows to client | Logical reads | CPU ms | Elapsed ms | Plan operators |
|-------|-------|---------------:|--------------:|-------:|-----------:|----------------|
| Wallet balance (overdraft check) | Before | 100000 | 288087 | 162 | 162 | Index Seek (IX_LedgerEntry_WalletId_CreatedAt), Clustered Index Seek (PK_LedgerEntry) |
| Wallet summary (balance, month in/out, last 5) | Before | 100000 | 288087 | 203 | 203 | Index Seek (IX_LedgerEntry_WalletId_CreatedAt), Clustered Index Seek (PK_LedgerEntry) |
| Reconciliation: one day of captures/refunds | Before | 122976 | 4179 | 68 | 87 | Clustered Index Scan (PK_PaymentOperation) |
| Wallet balance (overdraft check) | After | 1 | 963 | 10 | 10 | Stream Aggregate, Index Seek (IX_LedgerEntry_WalletId_CreatedAt) |
| Wallet summary (balance, month in/out, last 5) | After | 6 | 966 | 22 | 22 | Stream Aggregate, Index Seek (IX_LedgerEntry_WalletId_CreatedAt), Top |
| Reconciliation: one day of captures/refunds | After | 2035 | 42 | 1 | 1 | Index Seek (IX_PaymentOperation_CreatedAt_Type) |
