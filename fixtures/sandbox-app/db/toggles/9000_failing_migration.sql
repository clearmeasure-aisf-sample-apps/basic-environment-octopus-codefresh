-- Failing-migration toggle (CAP-GIT-010). The sandbox release pipeline copies this script into
-- the migrator image only when the marker file toggles/failing-migration exists in the commit,
-- so the PreSync Job fails, the Argo CD sync fails and the old version keeps serving.
THROW 50000, N'sandbox failing-migration toggle is on (toggles/failing-migration)', 1;
