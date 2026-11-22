import { describe, expect, it, vi } from 'vitest';
import type { DialogHost } from '../../client-api/DialogHost';
import { createRecordOpener, parseRecordOpenMode, sidePaneWidth } from './recordOpener';

function opener(mode: 'sidepane' | 'page') {
  const record = vi.fn(async () => ({ entityType: 'account', id: 'a1', name: 'Contoso' }));
  const navigate = vi.fn();
  const open = createRecordOpener({ appId: 'app1', mode, dialogs: { record } as unknown as DialogHost, navigate });
  return { open, record, navigate };
}

describe('recordOpener', () => {
  it('reads the setting, defaulting to the side pane', () => {
    expect(parseRecordOpenMode(undefined)).toBe('sidepane');
    expect(parseRecordOpenMode(' Page ')).toBe('page');
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    expect(parseRecordOpenMode('dialog')).toBe('sidepane');
    expect(warn).toHaveBeenCalledOnce();
    warn.mockRestore();
  });

  it('opens a side pane two thirds as wide as the form', async () => {
    const form = document.createElement('section');
    form.setAttribute('data-xrmverse-form', '');
    form.getBoundingClientRect = () => ({ width: 900 }) as DOMRect;
    const input = document.createElement('input');
    form.append(input);
    const { open, record, navigate } = opener('sidepane');
    const saved = await open({ etn: 'account', id: 'a1', state: { x: 1 } }, { anchor: input });
    expect(saved).toEqual({ entityType: 'account', id: 'a1', name: 'Contoso' });
    expect(navigate).not.toHaveBeenCalled();
    expect(record).toHaveBeenCalledWith({
      url: '/main.aspx?appid=app1&pagetype=entityrecord&etn=account&id=a1',
      position: 2,
      width: { value: 600, unit: 'px' },
      state: { x: 1 },
    });
  });

  it('falls back to two thirds of the window without a form', () => {
    expect(sidePaneWidth(null)).toEqual({ value: 66, unit: '%' });
  });

  it('opens the page with Ctrl or Cmd, or in page mode', async () => {
    const side = opener('sidepane');
    await side.open({ etn: 'account', id: 'a1', returnTo: '/back' }, { event: { metaKey: true } });
    expect(side.record).not.toHaveBeenCalled();
    expect(side.navigate).toHaveBeenCalledWith('/main.aspx?appid=app1&pagetype=entityrecord&etn=account&id=a1', { returnTo: '/back' });
    const page = opener('page');
    await page.open({ etn: 'account', id: 'a1' });
    expect(page.navigate).toHaveBeenCalledWith('/main.aspx?appid=app1&pagetype=entityrecord&etn=account&id=a1', undefined);
  });
});
