import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RibbonRule } from '../model/ribbon';
import { evaluate, parameterValues, UnsupportedRibbonParameter, type RibbonEnv } from './rules';

function env(over: Partial<Omit<RibbonEnv, 'valueOf'>> = {}): RibbonEnv {
  return {
    formContext: { marker: 'fc' },
    entity: 'xv_order',
    recordId: 'aaaa-bbbb',
    formId: 'form-1',
    formType: 'existing',
    readOnly: false,
    allDisabled: false,
    valueOf: (f) => (f === 'statuscode' ? { onForm: true, text: '1' } : f === 'flag' ? { onForm: true, text: 'true' } : { onForm: false, text: '' }),
    lcid: 1040,
    orgLcid: 1033,
    orgName: 'org1',
    resolve: () => Promise.reject(new Error('no library')),
    ...over,
  };
}

const fs = (state: string, extra: Partial<{ invert: boolean; dflt: boolean }> = {}): RibbonRule => ({ kind: 'formState', state, invert: false, dflt: false, ...extra });
const val = (field: string, value: string, extra: Partial<{ invert: boolean; dflt: boolean }> = {}): RibbonRule => ({ kind: 'value', field, value, invert: false, dflt: false, ...extra });
const cust = (extra: Partial<{ invert: boolean; dflt: boolean }> = {}): RibbonRule => ({
  kind: 'custom', library: 'lib.js', functionName: 'XV.check', parameters: [{ kind: 'string', value: 'x' }], invert: false, dflt: false, ...extra,
});

describe('evaluate', () => {
  it('empty_rules_hold', async () => expect(await evaluate([], env())).toBe(true));

  it('form_state_rules', async () => {
    expect(await evaluate([fs('Existing')], env())).toBe(true);
    expect(await evaluate([fs('Create')], env())).toBe(false);
    expect(await evaluate([fs('Create')], env({ formType: 'create' }))).toBe(true);
    expect(await evaluate([fs('ReadOnly')], env({ readOnly: true }))).toBe(true);
    expect(await evaluate([fs('ReadOnly')], env())).toBe(false);
    expect(await evaluate([fs('Disabled')], env({ allDisabled: true }))).toBe(true);
    expect(await evaluate([fs('BulkEdit')], env())).toBe(false);
    expect(await evaluate([fs('Create', { invert: true })], env())).toBe(true);
  });

  it('unknown_form_state_uses_default', async () => {
    expect(await evaluate([fs('Bogus')], env())).toBe(false);
    expect(await evaluate([fs('Bogus', { dflt: true })], env())).toBe(true);
  });

  it('value_rule_true_false_inverted', async () => {
    expect(await evaluate([val('statuscode', '1')], env())).toBe(true);
    expect(await evaluate([val('statuscode', '2')], env())).toBe(false);
    expect(await evaluate([val('statuscode', '1', { invert: true })], env())).toBe(false);
    expect(await evaluate([val('flag', 'true')], env())).toBe(true);
  });

  it('value_rule_on_a_missing_column_uses_default_and_invert_applies_to_it', async () => {
    expect(await evaluate([val('gone', '1')], env())).toBe(false);
    expect(await evaluate([val('gone', '1', { dflt: true })], env())).toBe(true);
    expect(await evaluate([val('gone', '1', { dflt: true, invert: true })], env())).toBe(false);
    expect(await evaluate([val('gone', '1', { invert: true })], env())).toBe(true);
  });

  it('entity_and_client_type_rules', async () => {
    const entity = (entityName: string, context?: string, invert = false): RibbonRule => ({ kind: 'entity', entity: entityName, ...(context ? { context } : {}), invert });
    expect(await evaluate([entity('xv_order')], env())).toBe(true);
    expect(await evaluate([entity('xv_other')], env())).toBe(false);
    expect(await evaluate([entity('xv_order', 'Form')], env())).toBe(true);
    expect(await evaluate([entity('xv_order', 'HomePageGrid')], env())).toBe(false);
    expect(await evaluate([entity('xv_order', 'SubGrid')], env())).toBe(false);
    expect(await evaluate([entity('xv_order', undefined, true)], env())).toBe(false);
    expect(await evaluate([{ kind: 'clientType', type: 'Web', invert: false }], env())).toBe(true);
    expect(await evaluate([{ kind: 'clientType', type: 'Outlook', invert: false }], env())).toBe(false);
    expect(await evaluate([{ kind: 'clientType', type: 'Web', invert: true }], env())).toBe(false);
  });

  it('or_rule_groups_are_and_inside', async () => {
    const or = (groups: RibbonRule[][], invert = false): RibbonRule => ({ kind: 'or', groups, invert });
    expect(await evaluate([or([[val('statuscode', '9')], [val('statuscode', '1')]])], env())).toBe(true);
    expect(await evaluate([or([[val('statuscode', '1'), val('flag', 'false')], [val('statuscode', '9')]])], env())).toBe(false);
    expect(await evaluate([or([[val('statuscode', '1'), val('flag', 'true')]])], env())).toBe(true);
    expect(await evaluate([or([[val('statuscode', '1')]], true)], env())).toBe(false);
    expect(await evaluate([or([])], env())).toBe(false);
  });

  it('rules_are_anded', async () => {
    expect(await evaluate([fs('Existing'), val('statuscode', '1')], env())).toBe(true);
    expect(await evaluate([fs('Existing'), val('statuscode', '2')], env())).toBe(false);
  });

  it('custom_rule_gets_parameters_and_its_boolean_or_promise_answer', async () => {
    const fn = vi.fn(() => true);
    const resolve = vi.fn(() => Promise.resolve(fn));
    expect(await evaluate([cust()], env({ resolve }))).toBe(true);
    expect(resolve).toHaveBeenCalledWith('lib.js', 'XV.check');
    expect(fn).toHaveBeenCalledWith('x');
    expect(await evaluate([cust()], env({ resolve: () => Promise.resolve(() => Promise.resolve(false)) }))).toBe(false);
    expect(await evaluate([cust({ invert: true })], env({ resolve: () => Promise.resolve(() => Promise.resolve(false)) }))).toBe(true);
  });

  it('custom_rule_throwing_uses_default', async () => {
    const boom = () => Promise.resolve(() => { throw new Error('boom'); });
    expect(await evaluate([cust()], env({ resolve: boom }))).toBe(false);
    expect(await evaluate([cust({ dflt: true })], env({ resolve: boom }))).toBe(true);
    expect(await evaluate([cust({ dflt: true })], env({ resolve: () => Promise.resolve(() => Promise.reject(new Error('x'))) }))).toBe(true);
    expect(await evaluate([cust({ dflt: true })], env({ resolve: () => Promise.reject(new Error('no lib')) }))).toBe(true);
  });

  describe('timeout', () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    it('custom_rule_promise_false_after_10s', async () => {
      const never = () => Promise.resolve(() => new Promise<boolean>(() => undefined));
      const p = evaluate([cust()], env({ resolve: never }));
      let done = false;
      void p.then(() => (done = true));
      await vi.advanceTimersByTimeAsync(9_999);
      expect(done).toBe(false);
      await vi.advanceTimersByTimeAsync(1);
      expect(await p).toBe(false);
    });

    it('timeout_is_final_false_but_a_throw_uses_default', async () => {
      const never = () => Promise.resolve(() => new Promise<boolean>(() => undefined));
      const t = evaluate([cust({ dflt: true, invert: true })], env({ resolve: never }));
      await vi.advanceTimersByTimeAsync(10_000);
      expect(await t).toBe(false);
      const t2 = evaluate([cust({ dflt: true })], env({ resolve: never }));
      await vi.advanceTimersByTimeAsync(10_000);
      expect(await t2).toBe(false);
      const boom = () => Promise.resolve(() => { throw new Error('boom'); });
      expect(await evaluate([cust({ dflt: true })], env({ resolve: boom }))).toBe(true);
    });

    it('abort_frees_a_pending_custom_rule_at_once_and_clears_the_timer', async () => {
      const never = () => Promise.resolve(() => new Promise<boolean>(() => undefined));
      const ctl = new AbortController();
      const p = evaluate([cust({ dflt: true })], env({ resolve: never }), ctl.signal);
      await vi.advanceTimersByTimeAsync(100);
      ctl.abort();
      expect(await p).toBe(false);
      expect(vi.getTimerCount()).toBe(0);
    });

    it('a_rule_that_answers_in_time_is_not_cut_off', async () => {
      const slow = () => Promise.resolve(() => new Promise<boolean>((r) => setTimeout(() => r(true), 9_000)));
      const p = evaluate([cust()], env({ resolve: slow }));
      await vi.advanceTimersByTimeAsync(9_000);
      expect(await p).toBe(true);
    });
  });

  it('an_aborted_evaluation_is_false_and_stops', async () => {
    const fn = vi.fn(() => true);
    const ctl = new AbortController();
    ctl.abort();
    expect(await evaluate([cust()], env({ resolve: () => Promise.resolve(fn) }), ctl.signal)).toBe(false);
    expect(fn).not.toHaveBeenCalled();
  });
});

describe('parameterValues', () => {
  it('crm_parameters_map_per_E4', () => {
    const e = env({ recordId: 'abcd-ef01' });
    const values = parameterValues(
      ['PrimaryControl', 'PrimaryEntityTypeName', 'FirstPrimaryItemId', 'PrimaryItemIds', 'PrimaryControlId', 'UserLcid', 'OrgLcid', 'OrgName'].map((value) => ({ kind: 'crm' as const, value })),
      e,
    );
    expect(values).toEqual([e.formContext, 'xv_order', '{ABCD-EF01}', ['{ABCD-EF01}'], 'form-1', 1040, 1033, 'org1']);
    expect(values[0]).toBe(e.formContext);
  });

  it('crm_item_ids_on_a_new_record_are_empty', () => {
    expect(parameterValues([{ kind: 'crm', value: 'FirstPrimaryItemId' }, { kind: 'crm', value: 'PrimaryItemIds' }], env({ recordId: undefined }))).toEqual(['', []]);
  });

  it('unknown_crm_parameter_throws', () => {
    expect(() => parameterValues([{ kind: 'crm', value: 'SelectedControl' }], env())).toThrow(UnsupportedRibbonParameter);
    expect(new UnsupportedRibbonParameter('X')).toBeInstanceOf(Error);
  });

  it('typed_parameters_convert', () => {
    expect(
      parameterValues(
        [
          { kind: 'string', value: ' a ' },
          { kind: 'bool', value: 'True' },
          { kind: 'bool', value: 'no' },
          { kind: 'int', value: '42' },
          { kind: 'decimal', value: '3.5' },
        ],
        env(),
      ),
    ).toEqual([' a ', true, false, 42, 3.5]);
    expect(() => parameterValues([{ kind: 'int', value: '4.5' }], env())).toThrow('IntParameter');
    expect(() => parameterValues([{ kind: 'decimal', value: 'abc' }], env())).toThrow('DecimalParameter');
  });
});
