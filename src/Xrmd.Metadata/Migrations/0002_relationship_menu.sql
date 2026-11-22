-- The associated menu configuration of a relationship, as the opaque XML of a solution file (navigation pane settings of
-- EntityRelationshipRoles), kept for the export: xrmverse has no navigation pane to apply it to.

ALTER TABLE meta.relationship ADD associated_menu_xml nvarchar(max) NULL;
GO
