-- Demo data for the execution-plan benchmark (docs/sql). Synthetic only: no real people, cards or money.
-- 5,001 wallets, 1,000,000 ledger rows (500,000 balanced pairs), one "hot" wallet with 100,000 rows,
-- 100,000 card payments with 125,000 operations (captures + every 4th payment partially refunded) over 60 days.
SET NOCOUNT ON;

DECLARE @start datetimeoffset = '2026-09-10T00:00:00+00:00';
DECLARE @hot uniqueidentifier = '11111111-1111-1111-1111-111111111111';

;WITH n AS (SELECT TOP (5000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.Wallet (Id, UserId, IsFrozen, CreatedAt)
SELECT NEWID(), CONCAT('bench-user-', i), 0, @start FROM n;
INSERT dbo.Wallet (Id, UserId, IsFrozen, CreatedAt) VALUES (@hot, 'bench-hot', 0, @start);

SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS rn INTO #w FROM dbo.Wallet WHERE UserId LIKE 'bench-user-%';
CREATE UNIQUE CLUSTERED INDEX cx ON #w (rn);

-- 500,000 pairs; every 10th pair debits and every other 10th pair credits the hot wallet (100,000 of its rows).
-- Materialised first: NEWID() in a CTE referenced twice would give the two sides different pair ids.
;WITH n AS (SELECT TOP (500000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
SELECT i,
       NEWID() AS PairId,
       NEWID() AS CorrelationId,
       CAST(((i * 7919) % 50000) / 100.0 + 1 AS decimal(18, 2)) AS Amount,
       DATEADD(MINUTE, (i * 17) % 86400, @start) AS At,
       CASE WHEN i % 10 = 0 THEN @hot ELSE (SELECT Id FROM #w WHERE rn = 1 + (i * 31) % 5000) END AS Debit,
       CASE WHEN i % 10 = 5 THEN @hot ELSE (SELECT Id FROM #w WHERE rn = 1 + (i * 37 + 11) % 5000) END AS Credit
INTO #p
FROM n;
DELETE FROM #p WHERE Debit = Credit;

INSERT dbo.LedgerEntry (Id, WalletId, Amount, PairId, CorrelationId, TransferId, Kind, Description, CreatedAt)
SELECT NEWID(), Debit, -Amount, PairId, CorrelationId, NULL, 0, 'benchmark', At FROM #p
UNION ALL
SELECT NEWID(), Credit, Amount, PairId, CorrelationId, NULL, 0, 'benchmark', At FROM #p;

;WITH n AS (SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
INSERT dbo.CardPayment (Id, UserId, ThreeDsSessionId, Provider, ProviderReference, Amount, CapturedAmount, RefundedAmount,
                        CardLast4, Status, DeclineCode, CorrelationId, CreatedAt, UpdatedAt)
SELECT NEWID(), CONCAT('bench-user-', 1 + i % 5000), NEWID(),
       CASE i % 3 WHEN 0 THEN 'simbank-rest' WHEN 1 THEN 'simbank-soap' ELSE 'simpay' END,
       CONCAT('BENCH-AUTH-', i), 100, 100, CASE WHEN i % 4 = 0 THEN 20 ELSE 0 END,
       '0010', 2, NULL, NEWID(), DATEADD(MINUTE, (i * 13) % 86400, @start), DATEADD(MINUTE, (i * 13) % 86400, @start)
FROM n;

-- Capture for every payment, a partial refund for every 4th (Refund = 2), a few failed ones (Status 2).
INSERT dbo.PaymentOperation (Id, PaymentId, Type, Status, Amount, Provider, ProviderReference, ProviderIdempotencyKey,
                             CorrelationId, CreatedAt, UpdatedAt)
SELECT NEWID(), Id, 1, CASE WHEN ABS(CHECKSUM(Id)) % 50 = 0 THEN 2 ELSE 1 END, 100, Provider,
       CONCAT('BENCH-CAP-', ProviderReference), CONCAT(CAST(Id AS char(36)), '-capture'), CorrelationId, CreatedAt, CreatedAt
FROM dbo.CardPayment WHERE UserId LIKE 'bench-user-%'
UNION ALL
SELECT NEWID(), Id, 2, 1, 20, Provider, CONCAT('BENCH-REF-', ProviderReference), CONCAT(CAST(Id AS char(36)), '-refund'),
       NEWID(), DATEADD(HOUR, 30, CreatedAt), DATEADD(HOUR, 30, CreatedAt)
FROM dbo.CardPayment WHERE UserId LIKE 'bench-user-%' AND RefundedAmount > 0;

UPDATE STATISTICS dbo.LedgerEntry WITH FULLSCAN;
UPDATE STATISTICS dbo.PaymentOperation WITH FULLSCAN;

SELECT (SELECT COUNT(*) FROM dbo.Wallet) AS Wallets,
       (SELECT COUNT(*) FROM dbo.LedgerEntry) AS LedgerRows,
       (SELECT COUNT(*) FROM dbo.LedgerEntry WHERE WalletId = @hot) AS HotWalletRows,
       (SELECT COUNT(*) FROM dbo.PaymentOperation) AS PaymentOperations;
