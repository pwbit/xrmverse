import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';

const root = join(__dirname, '..');
/** Files allowed to hold colour values: the theme, and the chart XML parser (colours there are customization data). */
const allowed = new Set(['shell/theme.ts', 'parsers/chartXml.ts']);

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === 'test' ? [] : sources(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name) ? [path] : [];
  });
}

describe('colours', () => {
  it('uses Fluent tokens instead of hex or rgb colours', () => {
    const offenders = sources(root)
      .map((path) => relative(root, path).split('\\').join('/'))
      .filter((file) => !allowed.has(file))
      .flatMap((file) =>
        readFileSync(join(root, file), 'utf8')
          .split('\n')
          .map((line, i) => ({ line, at: `${file}:${i + 1}` }))
          .filter(({ line }) => /#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6})\b|['"`][^'"`]*\brgba?\(/.test(line))
          .map(({ at }) => at),
      );
    expect(offenders).toEqual([]);
  });
});
