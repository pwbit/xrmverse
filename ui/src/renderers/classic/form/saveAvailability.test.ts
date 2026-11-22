import { describe, expect, it } from 'vitest';
import { saveAvailability } from './saveAvailability';

const state = (over: Partial<{ readOnly: boolean; dirty: boolean; busy: boolean }> = {}) => ({ readOnly: false, dirty: true, busy: false, ...over });

describe('saveAvailability', () => {
  it('is shown and enabled on a dirty, idle, writable form', () => {
    expect(saveAvailability({ record: undefined, canCreate: true }, state())).toEqual({ shown: true, disabled: false });
    expect(saveAvailability({ record: { id: 1 }, canCreate: false }, state())).toEqual({ shown: true, disabled: false });
  });

  it('is hidden on a read-only form', () => {
    expect(saveAvailability({ record: { id: 1 }, canCreate: true }, state({ readOnly: true })).shown).toBe(false);
  });

  it('is hidden on a create form without the create privilege', () => {
    expect(saveAvailability({ record: undefined, canCreate: false }, state()).shown).toBe(false);
  });

  it('is disabled when nothing changed', () => {
    expect(saveAvailability({ record: undefined, canCreate: true }, state({ dirty: false }))).toEqual({ shown: true, disabled: true });
  });

  it('is disabled while the form is busy', () => {
    expect(saveAvailability({ record: undefined, canCreate: true }, state({ busy: true }))).toEqual({ shown: true, disabled: true });
  });
});
