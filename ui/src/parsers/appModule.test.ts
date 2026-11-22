import { describe, expect, it } from 'vitest';
import { parseAppModule } from './appModule';

describe('parseAppModule', () => {
  it('has no siteMapId or description when absent', () => {
    const m = parseAppModule({ appmoduleid: 'i', uniquename: 'u', name: 'n', clienttype: 4 }, []);
    expect(m.siteMapId).toBeUndefined();
    expect(m.description).toBeUndefined();
    expect(m.components).toEqual({ entities: [], views: [], forms: [] });
  });
});
