import { act, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { attr } from '../admin/tables/testAttrs';
import { contextOf, field, formOf, RECORD_ID, section, simpleForm, tab } from '../renderers/classic/form/formTestSupport';
import type { Attribute } from './attribute';
import type { Control } from './control';
import type { FormContext } from './formContext';
import { mountForm } from './testForm';

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const code = attr('xv_code', { displayName: 'Code' });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha', xv_code: 'A-1' };

const ui = (fc: FormContext) => fc.ui as Record<string, (...a: unknown[]) => unknown>;
const data = (fc: FormContext) => fc.data as unknown as Record<string, (...a: unknown[]) => Promise<void>>;
const entity = (fc: FormContext) => fc.data.entity as Record<string, (...a: unknown[]) => unknown>;
const attribute = (fc: FormContext, n: string) => fc.getAttribute(n) as Attribute & Record<string, (...a: unknown[]) => unknown>;

describe('formContext', () => {
  it('refresh_ribbon_calls_the_host_callback_and_is_a_no_op_without_one', async () => {
    const onRefreshRibbon = vi.fn();
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD), { onRefreshRibbon });
    await screen.findByLabelText('xv_name label');
    ui(m.fc()).refreshRibbon();
    expect(onRefreshRibbon).toHaveBeenCalledTimes(1);
    const bare = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findAllByLabelText('xv_name label');
    expect(() => ui(bare.fc()).refreshRibbon()).not.toThrow();
  });

  it('get_id_braced_upper_and_empty_on_create', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findByLabelText('xv_name label');
    expect(entity(m.fc()).getId()).toBe(`{${RECORD_ID.toUpperCase()}}`);
    expect(entity(m.fc()).getEntityName()).toBe('xv_project');
    expect(entity(m.fc()).getEntitySetName()).toBe('xv_projects');
    expect(entity(m.fc()).getPrimaryAttributeValue()).toBe('Alpha');
    expect(entity(m.fc()).getEntityReference()).toEqual({ id: `{${RECORD_ID.toUpperCase()}}`, entityType: 'xv_project', name: 'Alpha' });
  });

  it('get_id_empty_on_create', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], undefined));
    await screen.findByLabelText('xv_name label');
    expect(entity(m.fc()).getId()).toBe('');
  });

  it('get_attribute_null_for_unknown', async () => {
    const m = mountForm(simpleForm([field('xv_name'), field('xv_code')]), contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    expect(fc.getAttribute('xv_nope')).toBeNull();
    expect(fc.getAttribute(7)).toBeNull();
    expect((fc.getAttribute(1) as Attribute).getName()).toBe('xv_code');
    expect((fc.getAttribute() as Attribute[]).map((a) => a.getName())).toEqual(['xv_name', 'xv_code']);
    expect((fc.getAttribute((a: Attribute) => a.getName() === 'xv_code') as Attribute[]).map((a) => a.getName())).toEqual(['xv_code']);
    expect(fc.data.entity.attributes.getLength()).toBe(2);
    expect(fc.getControl('xv_nope')).toBeNull();
  });

  it('get_control_by_name_index_predicate', async () => {
    const m = mountForm(simpleForm([field('xv_name'), field('xv_code')]), contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    expect((fc.getControl('xv_code') as Control).getName()).toBe('xv_code');
    expect((fc.getControl(0) as Control).getName()).toBe('xv_name');
    expect((fc.getControl((c: Control) => c.getName().endsWith('code')) as Control[]).map((c) => c.getName())).toEqual(['xv_code']);
    expect((fc.getControl() as Control[]).length).toBe(2);
    const names: string[] = [];
    fc.ui.controls.forEach((c) => names.push(c.getName()));
    expect(names).toEqual(['xv_name', 'xv_code']);
    expect(((fc.getControl('xv_code') as Control).getAttribute as () => Attribute)()).toBe(fc.getAttribute('xv_code'));
  });

  it('form_notification_levels_and_clear', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    act(() => {
      expect(ui(fc).setFormNotification('Check the budget', 'WARNING', 'budget')).toBe(true);
      expect(ui(fc).setFormNotification('Saved by script', 'INFO', 'info')).toBe(true);
      expect(ui(fc).setFormNotification('Broken', 'ERROR', 'err')).toBe(true);
    });
    // findByRole: on a slow runner the overlay subscription (a passive effect of the mount) can run after this act, and the
    // render it then schedules is not flushed by the act above.
    const region = await screen.findByRole('region', { name: 'Form notifications' });
    // The newest on top.
    expect(within(region).getAllByText(/Check the budget|Saved by script|Broken/).map((e) => e.textContent)).toEqual(['Broken', 'Saved by script', 'Check the budget']);
    expect(screen.getByText('Broken').closest('.fui-MessageBar')).toHaveAttribute('data-intent', 'error');
    expect(screen.getByText('Check the budget').closest('.fui-MessageBar')).toHaveAttribute('data-intent', 'warning');
    expect(screen.getByText('Saved by script').closest('.fui-MessageBar')).toHaveAttribute('data-intent', 'info');
    // The same id replaces its message.
    act(() => void ui(fc).setFormNotification('Check the budget again', 'WARNING', 'budget'));
    expect(screen.queryByText('Check the budget')).toBeNull();
    expect(screen.getByText('Check the budget again')).toBeInTheDocument();
    let cleared: unknown;
    act(() => void (cleared = ui(fc).clearFormNotification('budget')));
    expect(cleared).toBe(true);
    expect(screen.queryByText('Check the budget again')).toBeNull();
    expect(ui(fc).clearFormNotification('budget')).toBe(false);
    expect(ui(fc).setFormNotification('x', 'SEVERE', 'y')).toBe(false);
  });

  it('form_type_create_update_read_only', async () => {
    const created = mountForm(simpleForm([field('xv_name')]), contextOf([name], undefined));
    await screen.findByLabelText('xv_name label');
    expect(ui(created.fc()).getFormType()).toBe(1);
    document.body.innerHTML = '';
    const updated = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findByLabelText('xv_name label');
    expect(ui(updated.fc()).getFormType()).toBe(2);
    document.body.innerHTML = '';
    const readOnly = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD, { rights: ['Read'] }));
    await screen.findByLabelText('xv_name label');
    expect(ui(readOnly.fc()).getFormType()).toBe(3);
    document.body.innerHTML = '';
    const statecode = attr('statecode', { kind: 'choice', options: [{ value: 0, label: 'Active' }, { value: 1, label: 'Inactive' }] });
    const inactive = mountForm(simpleForm([field('xv_name')]), contextOf([name, statecode], { ...RECORD, statecode: 1 }));
    await screen.findByLabelText('xv_name label');
    expect(ui(inactive.fc()).getFormType()).toBe(4);
    expect(screen.getByText("Read-only: this record's status is Inactive.")).toBeInTheDocument();
  });

  it('form_selector_items_and_navigate_switches_form', async () => {
    // The OpenProject365 account script: find the item by id and navigate to it.
    const customer = { ...simpleForm([field('xv_name')]), id: 'f83260a8-d6cf-f111-a05b-7ced8d2d53e4', name: 'Customer' };
    const supplier = { ...simpleForm([field('xv_name')]), id: '2c3360a8-d6cf-f111-a05b-7ced8d2d53e4', name: 'Supplier' };
    const m = mountForm(customer, contextOf([name], RECORD), { forms: [customer, supplier] });
    await screen.findByLabelText('xv_name label');
    const selector = (m.fc().ui as unknown as { formSelector: { getCurrentItem(): Record<string, () => unknown>; items: { get(): Record<string, () => unknown>[]; forEach(f: (i: Record<string, () => unknown>) => void): void } } }).formSelector;
    expect(selector.getCurrentItem().getId()).toBe(customer.id);
    expect(selector.getCurrentItem().getLabel()).toBe('Customer');
    expect(selector.items.get().map((i) => i.getLabel())).toEqual(['Customer', 'Supplier']);
    selector.items.forEach((item) => {
      if (item.getId() === supplier.id) item.navigate();
    });
    expect(m.selected).toEqual([supplier.id]);
    // An item a script hides leaves the selector.
    expect(screen.getByRole('combobox', { name: 'Form' })).toBeInTheDocument();
    act(() => void (selector.items.get()[1]!.setVisible as (v: boolean) => void)(false));
    // A single form left: no selector, as with one form. The overlay store notifies at once; the page re-renders after it.
    await waitFor(() => expect(screen.queryByRole('combobox', { name: 'Form' })).toBeNull());
    expect((selector.items.get()[1]!.getVisible as () => boolean)()).toBe(false);
  });

  it('form_selector_with_one_form_has_no_current_item', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findByLabelText('xv_name label');
    expect((m.fc().ui as unknown as { formSelector: { getCurrentItem(): unknown } }).formSelector.getCurrentItem()).toBeNull();
  });

  it('data_save_returns_promise_resolving_after_save', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    act(() => attribute(fc, 'xv_name').setValue('Beta'));
    expect(fc.data.entity.getIsDirty).toBeDefined();
    expect((fc.data.getIsDirty as () => boolean)()).toBe(true);
    let saved: Promise<void>;
    await act(async () => {
      saved = data(fc).save();
      await saved;
    });
    expect(m.sent).toEqual([{ method: 'PATCH', body: { xv_name: 'Beta' } }]);
    // OnSave ran with mode 1 before the save.
    expect(m.formXml).toEqual(['onsave']);
    // A save that an OnSave handler prevents rejects.
    (fc.data.entity.addOnSave as (f: unknown) => void)((ctx: { getEventArgs(): { preventDefault(): void; getSaveMode(): number } }) => {
      expect(ctx.getEventArgs().getSaveMode()).toBe(1);
      ctx.getEventArgs().preventDefault();
    });
    await act(async () => {
      await expect(data(fc).save()).rejects.toMatchObject({ message: 'The save was canceled by an OnSave handler.' });
    });
    expect(m.sent).toHaveLength(1);
  });

  describe('async OnSave (app setting AsyncOnSave)', () => {
    type SaveArgs = { preventDefault(): void; preventDefaultOnError(): void; disableAsyncTimeout(): void; isDefaultPrevented(): boolean };
    type Ctx = { getEventArgs(): SaveArgs };
    const addOnSave = (fc: FormContext, fn: (ctx: Ctx) => unknown) => (fc.data.entity.addOnSave as (f: unknown) => void)(fn);
    const later = <T,>(ms: number, value?: T) => new Promise<T>((resolve) => setTimeout(() => resolve(value as T), ms));

    async function saveWith(asyncHandlers: { onLoad: boolean; onSave: boolean }, ...fns: ((ctx: Ctx) => unknown)[]) {
      const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD), { asyncHandlers });
      await screen.findByLabelText('xv_name label');
      const fc = m.fc();
      act(() => attribute(fc, 'xv_name').setValue('Beta'));
      for (const fn of fns) addOnSave(fc, fn);
      let outcome: unknown;
      await act(async () => {
        outcome = await data(fc).save().then(
          () => 'saved',
          (e: Error) => e.message,
        );
      });
      return { m, outcome };
    }

    it('the_save_waits_for_the_promise_of_each_handler', async () => {
      const order: string[] = [];
      const { m, outcome } = await saveWith({ onLoad: false, onSave: true }, () => later(20).then(() => order.push('first')), () => void order.push('second'));
      expect(order).toEqual(['first', 'second']);
      expect(outcome).toBe('saved');
      expect(m.sent).toHaveLength(1);
    });

    it('a_rejected_promise_is_reported_and_the_save_still_runs', async () => {
      const { m, outcome } = await saveWith({ onLoad: false, onSave: true }, () => Promise.reject(new Error('no')));
      expect(m.notes.map((n) => n.text)).toEqual(['Script error in anonymous: no']);
      expect(outcome).toBe('saved');
    });

    it('prevent_default_on_error_cancels_the_save_when_the_promise_rejects', async () => {
      const { m, outcome } = await saveWith({ onLoad: false, onSave: true }, (ctx) => {
        ctx.getEventArgs().preventDefaultOnError();
        return Promise.reject(new Error('invalid'));
      });
      expect(outcome).toBe('The save was canceled by an OnSave handler.');
      expect(m.sent).toEqual([]);
    });

    it('prevent_default_on_error_cancels_the_save_when_the_handler_throws', async () => {
      const { m, outcome } = await saveWith({ onLoad: false, onSave: false }, (ctx) => {
        ctx.getEventArgs().preventDefaultOnError();
        throw new Error('boom');
      });
      expect(outcome).toBe('The save was canceled by an OnSave handler.');
      expect(m.sent).toEqual([]);
    });

    it('prevent_default_on_error_of_one_handler_does_not_cover_the_next', async () => {
      const { m, outcome } = await saveWith(
        { onLoad: false, onSave: true },
        (ctx) => void ctx.getEventArgs().preventDefaultOnError(),
        () => Promise.reject(new Error('second')),
      );
      expect(outcome).toBe('saved');
      expect(m.sent).toHaveLength(1);
    });

    it('without_the_setting_the_save_does_not_wait', async () => {
      const order: string[] = [];
      const { outcome } = await saveWith({ onLoad: false, onSave: false }, () => later(20).then(() => order.push('late')));
      expect(outcome).toBe('saved');
      expect(order).toEqual([]);
      await act(() => later(30));
      expect(order).toEqual(['late']);
    });

    it('disable_async_timeout_is_available_and_is_a_no_op_without_the_setting', async () => {
      const { outcome } = await saveWith({ onLoad: false, onSave: false }, (ctx) => void ctx.getEventArgs().disableAsyncTimeout());
      expect(outcome).toBe('saved');
    });
  });

  it('data_refresh', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    const input = await screen.findByLabelText('xv_name label');
    const fc = m.fc();
    act(() => attribute(fc, 'xv_name').setValue('Changed'));
    expect(input).toHaveValue('Changed');
    const before = m.reads();
    await act(async () => {
      await data(fc).refresh();
    });
    expect(m.reads()).toBe(before + 1);
    await waitFor(() => expect(input).toHaveValue('Alpha'));
    // A refresh does not raise OnLoad or OnSave.
    expect(m.formXml).toEqual([]);
    // refresh(true) saves first.
    act(() => attribute(fc, 'xv_name').setValue('Gamma'));
    await act(async () => {
      await data(fc).refresh(true);
    });
    expect(m.sent).toEqual([{ method: 'PATCH', body: { xv_name: 'Gamma' } }]);
  });

  it('undocumented_member_undefined_documented_unimplemented_throws', async () => {
    const m = mountForm(simpleForm([field('xv_name')]), contextOf([name], RECORD));
    await screen.findByLabelText('xv_name label');
    const fc = m.fc() as unknown as Record<string, Record<string, unknown>>;
    expect(() => fc.data!.process).toThrow('formContext.data.process is not available in this version (stage after UI-1).');
    expect(() => (fc.data!.addOnLoad as () => void)()).toThrow('formContext.data.addOnLoad is not available in this version');
    expect(() => fc.ui!.navigation).toThrow('formContext.ui.navigation is not available in this version');
    expect(() => (fc.data!.entity as Record<string, () => void>).getDataXml()).toThrow('formContext.data.entity.getDataXml is not available');
    // Undocumented names stay undefined, as in Dataverse.
    expect(fc.data!.notAMember).toBeUndefined();
    expect(fc.ui!.footerSection).toBeUndefined();
    expect((fc as Record<string, unknown>).context).toBeUndefined();
  });

  it('tab_and_section_collections', async () => {
    const form = formOf([
      tab('summary', [{ width: '100%', sections: [section([[field('xv_name')]], { name: 'general', labels: { 1033: 'General' } })] }]),
      tab('details', [{ width: '100%', sections: [section([[field('xv_code')]], { name: 'more' })] }]),
    ]);
    const m = mountForm(form, contextOf([name, code], RECORD));
    await screen.findByLabelText('xv_name label');
    const tabs = m.fc().ui.tabs;
    expect(tabs.get().map((t) => t.getName())).toEqual(['summary', 'details']);
    const general = tabs.get('summary')!.sections.get('general')!;
    expect((general.getLabel as () => string)()).toBe('General');
    expect(general.controls.get().map((c) => c.getName())).toEqual(['xv_name']);
    expect((general.getParent as () => unknown)()).toBe(tabs.get('summary'));
    expect(((m.fc().getControl('xv_code') as Control).getParent as () => unknown)()).toBe(tabs.get('details')!.sections.get('more'));
  });
});
