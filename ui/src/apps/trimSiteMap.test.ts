import { describe, expect, it } from 'vitest';
import type { SiteMapModel, SubArea } from '../model/sitemap';
import { trimSiteMap } from './trimSiteMap';

const sub = (id: string, extra: Partial<SubArea> = {}): SubArea => ({
  id,
  titles: { 1033: id },
  kind: extra.entity ? 'entity' : 'dashboard',
  privileges: [],
  ...extra,
});

const model: SiteMapModel = {
  areas: [
    {
      id: 'sales',
      titles: { 1033: 'Sales' },
      groups: [
        { id: 'customers', titles: {}, subAreas: [sub('accounts', { entity: 'account' }), sub('contacts', { entity: 'contact' })] },
        { id: 'hidden', titles: {}, subAreas: [sub('roles', { entity: 'role' })] },
      ],
    },
    {
      id: 'settings',
      titles: { 1033: 'Settings' },
      groups: [{ id: 'security', titles: {}, subAreas: [sub('roles2', { entity: 'role' })] }],
    },
  ],
};
const SCHEMA = { account: 'Account', contact: 'Contact', role: 'Role' };
const ids = (m: SiteMapModel) =>
  m.areas.map((a) => [a.id, a.groups.map((g) => [g.id, g.subAreas.map((s) => s.id)])]);

describe('trimSiteMap', () => {
  it('trimSiteMap hides a subarea without prvRead and empties groups and areas', () => {
    const trimmed = trimSiteMap(model, new Set(['prvReadAccount']), SCHEMA);
    expect(ids(trimmed)).toEqual([['sales', [['customers', ['accounts']]]]]);
    // The input is not changed.
    expect(model.areas).toHaveLength(2);
  });

  it('a subarea whose table is unknown is hidden', () => {
    const m: SiteMapModel = { areas: [{ id: 'a', titles: {}, groups: [{ id: 'g', titles: {}, subAreas: [sub('x', { entity: 'nope' })] }] }] };
    expect(trimSiteMap(m, new Set(['prvReadnope']), SCHEMA).areas).toEqual([]);
  });

  it('trimSiteMap honours Privilege children', () => {
    const m: SiteMapModel = {
      areas: [
        {
          id: 'a',
          titles: {},
          groups: [
            {
              id: 'g',
              titles: {},
              subAreas: [
                sub('dash', { privileges: [{ entity: 'role', privilege: 'Read' }] }),
                sub('acc', { entity: 'account', privileges: [{ entity: 'contact', privilege: 'Read,Write' }] }),
                sub('acc2', { entity: 'account', privileges: [{ entity: 'contact', privilege: 'Read' }] }),
                sub('free'),
              ],
            },
          ],
        },
      ],
    };
    const trimmed = trimSiteMap(m, new Set(['prvReadAccount', 'prvReadContact', 'prvReadRole']), SCHEMA);
    expect(ids(trimmed)).toEqual([['a', [['g', ['dash', 'acc2', 'free']]]]]);
    const withoutRole = trimSiteMap(m, new Set(['prvReadAccount', 'prvReadContact', 'prvWriteContact']), SCHEMA);
    expect(ids(withoutRole)).toEqual([['a', [['g', ['acc', 'acc2', 'free']]]]]);
  });
});
