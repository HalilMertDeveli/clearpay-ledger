-- ClearPay ledger stored procedures (SQL Server). Created by the EF migration
-- AddLedgerProceduresAndCoveringIndexes, which runs this embedded file batch by batch (split on GO).
-- Every procedure is answered from a covering nonclustered index (no key lookups).

CREATE OR ALTER PROCEDURE dbo.usp_LedgerWalletBalance
    @WalletId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    -- Overdraft check inside the Serializable transfer/withdraw/refund transaction.
    -- Seek on IX_LedgerEntry_WalletId_CreatedAt (INCLUDE Amount): reads only this wallet's rows.
    SELECT CAST(ISNULL(SUM(e.Amount), 0) AS decimal(18, 2)) AS Balance
    FROM dbo.LedgerEntry AS e
    WHERE e.WalletId = @WalletId;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_LedgerWalletSummary
    @WalletId   uniqueidentifier,
    @MonthStart datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;
    -- Result 1: balance and this month's incoming / outgoing in one pass.
    SELECT
        CAST(ISNULL(SUM(e.Amount), 0) AS decimal(18, 2)) AS Balance,
        CAST(ISNULL(SUM(CASE WHEN e.CreatedAt >= @MonthStart AND e.Amount > 0 THEN e.Amount END), 0) AS decimal(18, 2)) AS MonthIncoming,
        CAST(ISNULL(-SUM(CASE WHEN e.CreatedAt >= @MonthStart AND e.Amount < 0 THEN e.Amount END), 0) AS decimal(18, 2)) AS MonthOutgoing
    FROM dbo.LedgerEntry AS e
    WHERE e.WalletId = @WalletId;

    -- Result 2: last five movements (backward range scan on the same index, no sort).
    SELECT TOP (5) e.CreatedAt, e.Kind, e.Amount, e.CorrelationId
    FROM dbo.LedgerEntry AS e
    WHERE e.WalletId = @WalletId
    ORDER BY e.CreatedAt DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_PaymentSettlementCandidates
    @From datetimeoffset,
    @To   datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;
    -- Nightly reconciliation: one business day of captures (Type 1) and refunds (Type 2) that did not fail (Status 2).
    -- Range seek on IX_PaymentOperation_CreatedAt_Type; Id comes with the clustered key.
    SELECT o.Id, o.Provider, o.Type, o.Status, o.ProviderReference, o.CorrelationId, o.Amount
    FROM dbo.PaymentOperation AS o
    WHERE o.CreatedAt >= @From
      AND o.CreatedAt < @To
      AND o.Type IN (1, 2)
      AND o.Status <> 2;
END
GO
