-- =============================================================================
-- Grants the 'sales' department access to ZRFC_READ_TABLES.
-- Run against the same SQL Server database as SapPermissions_setup.sql
-- (Auth:SqlConnectionString / the kongsberg database shared with
-- sql2005-bridge / Normanton-Nexus).
--
-- Backs SalesController's schedule-waterfall endpoint (Controllers/
-- SalesController.cs, Helpers/SalesHelpers.cs) — a rebuild of
-- N:\Config\Office\Templates\AT_46c\sd_waterfall.xltm inside the sales
-- department's page. No new function code needed: this reuses the same
-- 'ZRFC_READ_TABLES' code every other read-only department (logistics,
-- warehouse, finance, management) is already granted in
-- SapPermissions_setup.sql.
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM dbo.SapDepartmentPermissions
    WHERE Department = 'sales' AND FunctionName = 'ZRFC_READ_TABLES'
)
    INSERT INTO dbo.SapDepartmentPermissions (Department, FunctionName, GrantedBy)
    VALUES ('sales', 'ZRFC_READ_TABLES', 'migrate_sales_department_permission');
GO
