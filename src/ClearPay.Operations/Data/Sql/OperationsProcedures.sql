-- Operations service (database ClearPayOps, schema ops). Run by the migration AddSettlementProcedure.
-- Same shape the ledger used before reconciliation moved here (see README, T-SQL section):
-- a range seek on IX_PaymentOperation_CreatedAt_Type, covering, no key lookups.

CREATE OR ALTER PROCEDURE ops.usp_SettlementCandidates
    @From datetimeoffset,
    @To   datetimeoffset
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.OperationId, p.Provider, p.Type, p.Status, p.ProviderReference, p.MerchantReference, p.Amount
    FROM ops.PaymentOperation AS p
    WHERE p.CreatedAt >= @From
      AND p.CreatedAt < @To
      AND p.Type IN (N'Capture', N'Refund')
      AND p.Status <> N'Failed';
END
GO
