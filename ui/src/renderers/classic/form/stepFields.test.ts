import { describe, expect, it } from 'vitest';
import { createUiOverlay } from '../../../client-api/uiOverlay';
import type { Cell } from '../../../model/form';
import type { ControlEnv } from './controlEnv';
import { field, section, tab } from './formTestSupport';
import { stepAttributes } from './stepFields';

const spacer: Cell = { id: 'spacer', labels: {}, showLabel: false, visible: true, colSpan: 1, rowSpan: 1 };
const subgrid: Cell = {
  id: 'grid',
  labels: {},
  showLabel: true,
  visible: true,
  colSpan: 1,
  rowSpan: 1,
  control: { kind: 'subgrid', id: 'grid', relationship: 'r', targetEntity: 'xv_task', viewId: 'v', recordsPerPage: 5 } as Cell['control'],
};

describe('stepAttributes', () => {
  it('lists the visible, enabled field cells of the visible sections, once each', () => {
    const name = field('xv_name');
    const scripted = field('xv_scripted');
    const hiddenCell = field('xv_hidden', { visible: false });
    const disabled = field('xv_disabled', {}, { disabled: true });
    const enabledByScript = field('xv_enabled', {}, { disabled: true });
    const disabledByScript = field('xv_off');
    const hiddenSection = section([[field('xv_insection')]], { name: 'gone', visible: false });
    const scriptHiddenSection = section([[field('xv_scriptsection')]], { name: 'scripted' });
    const t = tab('main', [
      { width: '50%', sections: [section([[name, spacer], [scripted, subgrid], [hiddenCell], [disabled], [enabledByScript], [disabledByScript]]), hiddenSection] },
      { width: '50%', sections: [scriptHiddenSection, section([[field('xv_name', { id: 'again' })], [field('xv_note')]], { name: 'second' })] },
    ]);
    const overlay = createUiOverlay();
    overlay.setElement(scripted, { visible: false });
    overlay.setElement(enabledByScript, { disabled: false });
    overlay.setElement(disabledByScript, { disabled: true });
    overlay.setElement(scriptHiddenSection, { visible: false });
    const env = { overlay } as ControlEnv;
    expect(stepAttributes(t, env)).toEqual(['xv_name', 'xv_enabled', 'xv_note']);
  });

  it('a cell hidden in the FormXml that a script shows is listed', () => {
    const cell = field('xv_name', { visible: false });
    const overlay = createUiOverlay();
    overlay.setElement(cell, { visible: true });
    expect(stepAttributes(tab('main', [{ width: '100%', sections: [section([[cell]])] }]), { overlay } as ControlEnv)).toEqual(['xv_name']);
  });

  it('works without an overlay (the FormXml decides)', () => {
    const t = tab('main', [{ width: '100%', sections: [section([[field('xv_name')], [field('xv_hidden', { visible: false })]])] }]);
    expect(stepAttributes(t, {} as ControlEnv)).toEqual(['xv_name']);
  });
});
