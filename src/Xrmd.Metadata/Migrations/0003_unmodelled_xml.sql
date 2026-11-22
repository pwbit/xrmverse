-- What a solution file gives a table, a column, a relationship and a global option set that xrmverse does not model
-- (auditing, search and customization flags, DisplayMask, any element xrmverse does not know), kept as opaque XML with
-- the positions of what xrmverse models, for the export (one generic mechanism: UnmodelledXml).

ALTER TABLE meta.entity ADD unmodelled_xml nvarchar(max) NULL;
GO

ALTER TABLE meta.attribute ADD unmodelled_xml nvarchar(max) NULL;
GO

ALTER TABLE meta.relationship ADD unmodelled_xml nvarchar(max) NULL;
GO

ALTER TABLE meta.optionset ADD unmodelled_xml nvarchar(max) NULL;
GO
