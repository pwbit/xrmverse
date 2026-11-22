import { describe, expect, it } from 'vitest';
import type { ChoiceOption } from './columnKinds';
import { moveBody } from './moveCard';

const choice = (type: 'Picklist' | 'Status' | 'State' | 'Boolean', options: ChoiceOption[]) => ({ kind: 'choice' as const, type, options });

describe('moveBody', () => {
  it('picklist sets the value', () => {
    const column = choice('Picklist', [{ value: 1, label: 'A' }, { value: 3, label: 'C' }]);
    expect(moveBody('industrycode', column, { industrycode: 1 }, 3)).toEqual({ industrycode: 3 });
  });

  it('statuscode to an option of another state sets the state too', () => {
    const column = choice('Status', [{ value: 1, label: 'Active', state: 0 }, { value: 2, label: 'Inactive', state: 1 }]);
    expect(moveBody('statuscode', column, { statuscode: 1, statecode: 0 }, 2)).toEqual({ statuscode: 2, statecode: 1 });
  });

  it('statuscode to an option of the same state sends the status only', () => {
    const column = choice('Status', [{ value: 1, label: 'Open', state: 0 }, { value: 5, label: 'Waiting', state: 0 }]);
    expect(moveBody('statuscode', column, { statuscode: 1, statecode: 0 }, 5)).toEqual({ statuscode: 5 });
  });

  it('state sets the state', () => {
    const column = choice('State', [{ value: 0, label: 'Active' }, { value: 1, label: 'Inactive' }]);
    expect(moveBody('statecode', column, { statecode: 0 }, 1)).toEqual({ statecode: 1 });
  });

  it('boolean sets true or false', () => {
    const column = choice('Boolean', [{ value: false, label: 'Allow' }, { value: true, label: 'Do Not Allow' }]);
    expect(moveBody('donotemail', column, { donotemail: false }, true)).toEqual({ donotemail: true });
  });

  it('none clears the value', () => {
    const column = choice('Picklist', [{ value: 1, label: 'A' }]);
    expect(moveBody('industrycode', column, { industrycode: 1 }, null)).toEqual({ industrycode: null });
  });
});
