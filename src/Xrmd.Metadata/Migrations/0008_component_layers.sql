-- Managed solutions (plan 2.6, rulings M1 and M2): the layers of each solution component and the rank of each managed
-- solution. A component's effective state stays in meta.* and data.*; its layers record which solution delivered what.
--
-- component_layer: one row per (component, owner). The owner is a managed solution (its Managed layer, payload = the
-- component as that solution delivered it), the System layer id (the base state of a seeded component) or the Active
-- solution (the unmanaged marker: payload NULL, layer_order the largest bigint). Type 3 is recorded as 10.
-- layer_order = segment * 1024 + sub of the owner's rank; System = 0. The top row wins.

CREATE TABLE meta.component_layer (
    component_type int              NOT NULL,
    object_id      uniqueidentifier NOT NULL,
    solution_id    uniqueidentifier NOT NULL,
    layer_order    bigint           NOT NULL,
    payload        nvarchar(max)    NULL,
    created_on     datetime2        NOT NULL CONSTRAINT DF_meta_component_layer_created_on DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_meta_component_layer PRIMARY KEY (component_type, object_id, solution_id)
);
GO

CREATE INDEX IX_meta_component_layer_solution ON meta.component_layer (solution_id);
GO

CREATE INDEX IX_meta_component_layer_object ON meta.component_layer (object_id);
GO

-- solution_rank: where a managed solution's layers sit. A base solution takes the next segment (sub 0); sub 1..1022 is
-- reserved for patches; a holding solution {name}_Upgrade takes its base's segment with sub 1023.

CREATE TABLE meta.solution_rank (
    solution_id uniqueidentifier NOT NULL CONSTRAINT PK_meta_solution_rank PRIMARY KEY,
    segment     int              NOT NULL,
    sub         int              NOT NULL,
    CONSTRAINT CK_meta_solution_rank_sub CHECK (sub BETWEEN 0 AND 1023),
    CONSTRAINT CK_meta_solution_rank_segment CHECK (segment > 0)
);
GO
