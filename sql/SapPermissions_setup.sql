-- =============================================================================
-- SapServer Permission Tables
-- Run this script against the same SQL Server database used by sql2005-bridge.
-- =============================================================================

-- Maps portal departments to the RFC functions they are allowed to call.
-- Add or remove rows here to control access without redeploying either service.
-- The wildcard function name '*' grants that department access to every RFC.
CREATE TABLE dbo.SapDepartmentPermissions
(
    Department   NVARCHAR(50)  NOT NULL,   -- matches PortalUserDepartments.Department
    FunctionName NVARCHAR(100) NOT NULL,   -- RFC function name, or '*' for all
    GrantedBy    NVARCHAR(80)  NOT NULL,
    GrantedAt    DATETIME      NOT NULL DEFAULT GETDATE(),
    CONSTRAINT PK_SapDepartmentPermissions PRIMARY KEY (Department, FunctionName)
);
GO

-- =============================================================================
-- Default permissions — adjust to match your SAP landscape
-- =============================================================================

INSERT INTO dbo.SapDepartmentPermissions (Department, FunctionName, GrantedBy)
VALUES
    -- Logistics can create transfer orders and read data
    ('logistics',   'L_TO_CREATE_SINGLE',   'setup'),
    ('logistics',   'ZRFC_READ_TABLES',     'setup'),
    -- Warehouse can also create transfer orders
    ('warehouse',   'L_TO_CREATE_SINGLE',   'setup'),
    ('warehouse',   'ZRFC_READ_TABLES',     'setup'),
    -- Finance and management read-only
    ('finance',     'ZRFC_READ_TABLES',     'setup'),
    ('management',  'ZRFC_READ_TABLES',     'setup');
GO

-- =============================================================================
-- Verification query — run after setup to confirm the schema looks correct
-- =============================================================================

-- SELECT
--     u.Username,
--     pud.Department,
--     sdp.FunctionName
-- FROM dbo.PortalUsers               u
-- INNER JOIN dbo.PortalUserDepartments     pud ON pud.UserID     = u.UserID
-- INNER JOIN dbo.SapDepartmentPermissions  sdp ON sdp.Department = pud.Department
-- WHERE u.IsActive = 1
-- ORDER BY u.Username, sdp.FunctionName;
