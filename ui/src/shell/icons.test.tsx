import { render } from '@testing-library/react';
import type { ComponentType } from 'react';
import { describe, expect, it } from 'vitest';
import * as icons from './icons';

/** Every icon is bundled (filled + regular), so the modern renderer can show the filled variant; the filter pair is a state, not a style. */
describe('shell icons', () => {
  const NOT_BUNDLED = new Set(['FilterIcon', 'FilterAppliedIcon']);
  const all: [string, ComponentType][] = [
    ...Object.entries(icons).filter(([name, v]) => typeof v === 'function' || (typeof v === 'object' && v !== null && name.endsWith('Icon'))) as [string, ComponentType][],
    ...Object.entries(icons.SITEMAP_TABLE_ICONS).map(([k, v]) => [`SITEMAP_TABLE_ICONS.${k}`, v] as [string, ComponentType]),
    ...Object.entries(icons.RECORD_TYPE_ICONS).map(([k, v]) => [`RECORD_TYPE_ICONS.${k}`, v] as [string, ComponentType]),
  ].filter(([name]) => !NOT_BUNDLED.has(name));

  it.each(all)('%s renders both a filled and a regular variant', (_name, Icon) => {
    const { container } = render(<Icon />);
    expect(container.querySelector('.fui-Icon-filled')).not.toBeNull();
    expect(container.querySelector('.fui-Icon-regular')).not.toBeNull();
  });
});
