-- Test-only additions to the SRM-CustomEntities sample, covering object kinds and states that the samples don't
-- use: a non-dbo schema, sequences, rules bound to types, computed / rowversion / xml / sql_variant columns, check
-- and unique constraints, a disabled FK, a filtered index, a non-PK clustered index, an indexed view, a synonym,
-- a disabled trigger, a database DDL trigger and extended properties.
CREATE SCHEMA audit
;
CREATE SEQUENCE audit.EVENT_NUMBERS AS bigint START WITH 1000 INCREMENT BY 5
;
CREATE RULE audit.R_POSITIVE AS @value >= 0
;
CREATE TYPE audit.POSITIVE_INT FROM int NOT NULL
;
EXEC sys.sp_bindrule N'audit.R_POSITIVE', N'audit.POSITIVE_INT'
;
CREATE TABLE audit.EVENTS
(
    EVENT_ID               int IDENTITY (10, 10) NOT NULL CONSTRAINT PK_AUDIT_EVENTS PRIMARY KEY NONCLUSTERED,
    EVENT_NUMBER           bigint                NOT NULL CONSTRAINT DF_AUDIT_EVENTS_NUMBER DEFAULT (NEXT VALUE FOR audit.EVENT_NUMBERS),
    EVENT_CODE             varchar(20)           NOT NULL CONSTRAINT UQ_AUDIT_EVENTS_CODE UNIQUE,
    AMOUNT                 decimal(12, 2)        NULL CONSTRAINT CK_AUDIT_EVENTS_AMOUNT CHECK (AMOUNT >= 0),
    QUANTITY               audit.POSITIVE_INT    DEFAULT 1,
    TOTAL AS (AMOUNT * QUANTITY) PERSISTED,
    PAYLOAD                xml                   NULL,
    ANYTHING               sql_variant           NULL,
    ROW_VERSION            rowversion,
    CREATED                datetimeoffset(3)     NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    CUS_ENTITY_DATA_SEQ_ID bigint                NULL CONSTRAINT FK_AUDIT_EVENTS_ENTITY REFERENCES dbo.CUS_ENTITY_DATA (SEQ_ID)
)
;
ALTER TABLE audit.EVENTS NOCHECK CONSTRAINT FK_AUDIT_EVENTS_ENTITY
;
CREATE CLUSTERED INDEX CIX_AUDIT_EVENTS_CREATED ON audit.EVENTS (CREATED DESC)
;
CREATE INDEX IX_AUDIT_EVENTS_AMOUNT ON audit.EVENTS (AMOUNT) INCLUDE (QUANTITY) WHERE AMOUNT IS NOT NULL
;
CREATE VIEW audit.EVENT_COUNTS WITH SCHEMABINDING AS
SELECT EVENT_CODE, COUNT_BIG(*) AS EVENT_COUNT FROM audit.EVENTS GROUP BY EVENT_CODE
;
CREATE UNIQUE CLUSTERED INDEX UCX_EVENT_COUNTS ON audit.EVENT_COUNTS (EVENT_CODE)
;
CREATE SYNONYM audit.ENTITY_DATA FOR dbo.CUS_ENTITY_DATA
;
CREATE TRIGGER audit.TR_EVENTS_NOOP ON audit.EVENTS AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
END
;
CREATE TRIGGER TR_DDL_NOOP ON DATABASE FOR CREATE_PROCEDURE AS
BEGIN
    SET NOCOUNT ON;
END
;
EXEC sys.sp_addextendedproperty N'MS_Description', N'Audit trail', N'SCHEMA', N'audit', N'TABLE', N'EVENTS'
;
EXEC sys.sp_addextendedproperty N'MS_Description', N'Business key', N'SCHEMA', N'audit', N'TABLE', N'EVENTS', N'COLUMN', N'EVENT_CODE'
;
EXEC sys.sp_addextendedproperty N'Owner', N'Audit team', N'SCHEMA', N'audit'
;
EXEC sys.sp_addextendedproperty N'Version', 42
;
