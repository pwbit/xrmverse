import { describe, expect, it } from 'vitest';
import { parseAppOnLoad } from './appOnLoadSetting';

describe('parseAppOnLoad', () => {
  it('empty_values_mean_no_handlers', () => {
    for (const v of [undefined, '', '  \n']) expect(parseAppOnLoad(v)).toEqual({ handlers: [] });
  });

  it('parses_handlers', () => {
    expect(parseAppOnLoad('[{"library":"a.js","function":"A.b"}]')).toEqual({ handlers: [{ library: 'a.js', function: 'A.b' }] });
  });

  it('rejects_wrong_shapes', () => {
    for (const v of ['{}', '[{"library":1,"function":"x"}]', '[{"library":"a.js"}]', 'not json', '[null]']) {
      const r = parseAppOnLoad(v);
      expect(r, v).toHaveProperty('error');
      expect((r as { error: string }).error).toMatch(/xrmverse_AppOnLoad/);
    }
    expect((parseAppOnLoad('not json') as { error: string }).error).toMatch(/JSON/);
    expect((parseAppOnLoad('{}') as { error: string }).error).toMatch(/array/);
    expect((parseAppOnLoad('[{"library":"a.js"}]') as { error: string }).error).toMatch(/"function"/);
  });

  it('rejects_a_library_outside_web_resources', () => {
    for (const lib of ['../x.js', 'https://evil/x.js', '/abs.js', '']) {
      expect(parseAppOnLoad(JSON.stringify([{ library: lib, function: 'f' }])), lib).toHaveProperty('error');
    }
  });
});
