import type { AttributeMeta, EntityMeta } from './metadata';

/** An attribute with defaults of a plain, optional, writable string column; tests override what they care about. */
export function attr(logicalName: string, over: Partial<AttributeMeta> = {}): AttributeMeta {
  return {
    logicalName,
    displayName: logicalName,
    kind: 'string',
    typeName: 'String',
    requiredLevel: 'None',
    required: false,
    validForCreate: true,
    validForUpdate: true,
    validForRead: true,
    isPrimaryId: false,
    isPrimaryName: false,
    isCustom: true,
    targets: [],
    options: [],
    secured: false,
    ...over,
  };
}

export function entity(attributes: AttributeMeta[], over: Partial<EntityMeta> = {}): EntityMeta {
  return {
    logicalName: 'xv_project',
    entitySetName: 'xv_projects',
    displayName: 'Project',
    primaryIdAttribute: 'xv_projectid',
    primaryNameAttribute: 'xv_name',
    isCustom: true,
    attributes,
    ...over,
  };
}
