import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { attr } from '../admin/tables/testAttrs';
import type { FormContext } from '../client-api/formContext';
import { mountForm } from '../client-api/testForm';
import type { Cell } from '../model/form';
import { contextOf, field, RECORD_ID, renderForm, simpleForm } from '../renderers/classic/form/formTestSupport';
import { standardControl, virtualControl, type TestControl } from '../test/pcf/controls';
import { probeControl, serveControls } from '../test/pcf/serve';
import { resetPcfLoader, setScriptLoader } from './loader';

/* eslint-disable @typescript-eslint/no-explicit-any */
type Ctx = any;

const globals = window as unknown as Record<string, unknown>;

/** What the host did to the probe controls of a test. */
interface Probe {
  calls: string[];
  contexts: Ctx[];
  notify?: () => void;
  container?: HTMLDivElement;
  outputs: Record<string, unknown>;
  state?: Record<string, unknown>;
  failInit?: boolean;
}
let probe: Probe;

/** A standard (or virtual) control class that records each call into `probe`. */
function probeClass(virtual = false) {
  return class {
    init(context: Ctx, notify: () => void, state: Record<string, unknown>, container?: HTMLDivElement) {
      probe.calls.push('init');
      if (probe.failInit) throw new Error('boom in init');
      probe.notify = notify;
      probe.state = state;
      probe.container = container;
      probe.contexts.push(context);
    }
    updateView(context: Ctx) {
      probe.calls.push(`updateView:${(context.updatedProperties as string[]).join(',')}`);
      probe.contexts.push(context);
      const text = `value ${String(context.parameters.value.raw)}`;
      if (virtual) return createElement('span', null, text);
      if (probe.container) probe.container.textContent = text;
      return undefined;
    }
    getOutputs() {
      return probe.outputs;
    }
    destroy() {
      probe.calls.push('destroy');
    }
  };
}

beforeEach(() => {
  resetPcfLoader();
  probe = { calls: [], contexts: [], outputs: {} };
  globals.__xvProbe = probeClass();
});
afterEach(() => {
  setScriptLoader(undefined);
  resetPcfLoader();
  for (const k of ['Reactv16', 'FluentUIReactv940', 'FluentUIReactv9', 'ComponentFramework', '__xvProbe']) delete globals[k];
  document.head.querySelectorAll('link[data-pcf]').forEach((l) => l.remove());
});

const name = attr('xv_name', { isPrimaryName: true, displayName: 'Name' });
const amount = attr('xv_amount', { kind: 'decimal', typeName: 'Decimal', displayName: 'Amount', precision: 2 });
const count = attr('xv_count', { kind: 'int', typeName: 'Integer', displayName: 'Count' });
const budget = attr('xv_budget', { kind: 'money', typeName: 'Money', displayName: 'Budget', precision: 2 });
const RECORD = { xv_projectid: RECORD_ID, xv_name: 'Alpha', xv_amount: 3, xv_count: 4, xv_budget: 1234.5 };

/** A cell of `attribute` whose FormXml names the PCF control `control` for the web. */
const pcfField = (control: TestControl, attribute = 'xv_amount', id = attribute): Cell =>
  field(attribute, { id: `cell-${id}` }, { id, customControl: { name: control.name, parameters: { value: { value: attribute, static: false } } } });

const last = (): Ctx => probe.contexts[probe.contexts.length - 1];
const count_ = (call: string) => probe.calls.filter((c) => c === call).length;
const attribute = (fc: FormContext, n: string) => fc.getAttribute(n) as unknown as { getValue(): unknown; setValue(v: unknown): void };

function mountProbe(control: TestControl, over: { rights?: ('Read' | 'Write')[]; cells?: Cell[] } = {}) {
  const ctx = contextOf([name, amount, count], RECORD, { rights: over.rights });
  return mountForm(simpleForm(over.cells ?? [field('xv_name'), pcfField(control)]), ctx, { setup: () => serveControls([control]) });
}

describe('PcfFieldControl', () => {
  it('standard_control_init_once_and_update_view_on_value_change', async () => {
    const control = probeControl('Probe');
    const m = mountProbe(control);
    await waitFor(() => expect(probe.calls).toContain('updateView:value'));
    expect(probe.container?.textContent).toBe('value 3');
    expect(last().parameters.value.raw).toBe(3);
    act(() => attribute(m.fc(), 'xv_amount').setValue(5));
    await waitFor(() => expect(probe.container?.textContent).toBe('value 5'));
    expect(probe.calls.at(-1)).toBe('updateView:value');
    expect(count_('init')).toBe(1);
    expect(last().mode.isControlDisabled).toBe(false);
    expect(last().mode.label).toBe('xv_amount label');
  });

  it('notify_output_changed_writes_the_value_as_a_user_edit', async () => {
    const control = probeControl('Probe');
    const m = mountProbe(control);
    await waitFor(() => expect(probe.notify).toBeDefined());
    probe.outputs = { value: 7 };
    act(() => probe.notify!());
    await waitFor(() => expect(attribute(m.fc(), 'xv_amount').getValue()).toBe(7));
    expect((m.fc().data.entity.getIsDirty as () => boolean)()).toBe(true);
    expect(m.formXml).toContain('onchange:xv_amount');
    await waitFor(() => expect(probe.container?.textContent).toBe('value 7'));
  });

  it('an_output_of_the_wrong_type_is_a_field_error_and_is_not_set', async () => {
    const control = probeControl('Probe');
    const m = mountProbe(control);
    await waitFor(() => expect(probe.notify).toBeDefined());
    probe.outputs = { value: 'many' };
    act(() => probe.notify!());
    expect(await screen.findByText(/gave a value that does not fit/)).toBeInTheDocument();
    expect(attribute(m.fc(), 'xv_amount').getValue()).toBe(3);
    expect(m.formXml).not.toContain('onchange:xv_amount');
    // A correct value clears the error.
    probe.outputs = { value: 8 };
    act(() => probe.notify!());
    await waitFor(() => expect(screen.queryByText(/gave a value that does not fit/)).toBeNull());
    expect(attribute(m.fc(), 'xv_amount').getValue()).toBe(8);
  });

  it('virtual_control_element_is_rendered_and_re_rendered_without_a_second_init', async () => {
    globals.__xvProbe = probeClass(true);
    const control = probeControl('VProbe', { type: 'virtual' });
    const m = mountProbe(control);
    expect(await screen.findByText('value 3')).toBeInTheDocument();
    for (const v of [4, 5, 6, 7]) act(() => attribute(m.fc(), 'xv_amount').setValue(v));
    expect(await screen.findByText('value 7')).toBeInTheDocument();
    expect(count_('init')).toBe(1);
    // A virtual control gets no container.
    expect(probe.container).toBeUndefined();
  });

  it('virtual_test_control_renders_with_the_host_fluent_ui', async () => {
    const ctx = contextOf([name, count], RECORD);
    mountForm(simpleForm([pcfField(virtualControl, 'xv_count')]), ctx, { setup: () => serveControls([virtualControl]) });
    expect(await screen.findByText('Badge 4')).toBeInTheDocument();
  });

  it('read_only_form_disables_the_control_and_ignores_its_output', async () => {
    const control = probeControl('Probe');
    const m = mountProbe(control, { rights: ['Read'] });
    await waitFor(() => expect(probe.notify).toBeDefined());
    expect(last().mode.isControlDisabled).toBe(true);
    expect(last().parameters.value.security.editable).toBe(false);
    probe.outputs = { value: 9 };
    act(() => probe.notify!());
    await new Promise((r) => setTimeout(r, 20));
    expect(attribute(m.fc(), 'xv_amount').getValue()).toBe(3);
    expect((m.fc().data.entity.getIsDirty as () => boolean)()).toBe(false);
  });

  it('set_disabled_drives_mode_and_control_type_is_customcontrol', async () => {
    const control = probeControl('Probe');
    const m = mountProbe(control);
    await waitFor(() => expect(probe.notify).toBeDefined());
    const ctl = m.fc().getControl('xv_amount') as unknown as Record<string, (...a: unknown[]) => unknown>;
    await waitFor(() => expect(ctl.getControlType!()).toBe('customcontrol'));
    act(() => void ctl.setDisabled!(true));
    await waitFor(() => expect(last().mode.isControlDisabled).toBe(true));
    act(() => void ctl.setLabel!('Progress'));
    await waitFor(() => expect(last().mode.label).toBe('Progress'));
    expect((m.fc().getControl('xv_name') as unknown as { getControlType(): string }).getControlType()).toBe('standard');
  });

  it('track_container_resize_sends_layout', async () => {
    const observers: { callback: ResizeObserverCallback; disconnected: boolean }[] = [];
    const original = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class {
      private entry: { callback: ResizeObserverCallback; disconnected: boolean };
      constructor(callback: ResizeObserverCallback) {
        this.entry = { callback, disconnected: false };
        observers.push(this.entry);
      }
      observe() {}
      unobserve() {}
      disconnect() {
        this.entry.disconnected = true;
      }
    } as unknown as typeof ResizeObserver;
    try {
      const control = probeControl('Probe');
      mountProbe(control);
      await waitFor(() => expect(probe.notify).toBeDefined());
      expect(last().mode.allocatedWidth).toBe(-1);
      act(() => last().mode.trackContainerResize(true));
      expect(observers).toHaveLength(1);
      act(() => observers[0]!.callback([{ contentRect: { width: 320, height: 48 } } as ResizeObserverEntry], {} as ResizeObserver));
      await waitFor(() => expect(last().updatedProperties).toContain('layout'));
      expect(last().mode.allocatedWidth).toBe(320);
      expect(last().mode.allocatedHeight).toBe(48);
      cleanup();
      expect(observers[0]!.disconnected).toBe(true);
    } finally {
      globalThis.ResizeObserver = original;
    }
  });

  it('exception_in_init_falls_back_to_the_standard_control_with_a_notice', async () => {
    probe.failInit = true;
    const control = probeControl('Boom');
    mountProbe(control);
    expect(await screen.findByText("Custom control 'xv_XvTest.Boom' could not be loaded: it failed to start (boom in init)")).toBeInTheDocument();
    expect(await screen.findByLabelText('xv_amount label')).toHaveValue('3.00');
    // The other fields work.
    const nameBox = screen.getByLabelText('xv_name label');
    await userEvent.clear(nameBox);
    await userEvent.type(nameBox, 'Beta');
    expect(nameBox).toHaveValue('Beta');
  });

  it('a_bundle_that_throws_on_load_falls_back', async () => {
    const control: TestControl = { ...probeControl('Broken'), files: { 'bundle.js': 'throw new Error("bad bundle");' } };
    const m = mountProbe(control);
    expect(await screen.findByText(/^Custom control 'xv_XvTest\.Broken' could not be loaded: bundle\.js could not be loaded/)).toBeInTheDocument();
    expect(screen.getByLabelText('xv_amount label')).toHaveValue('3.00');
    expect((m.fc().getControl('xv_amount') as unknown as { getControlType(): string }).getControlType()).toBe('standard');
  });

  it('a_bound_property_on_a_missing_column_falls_back', async () => {
    const control = probeControl('Two', { extra: '<property name="other" display-name-key="O" of-type="Decimal" usage="bound" required="false" />' });
    const cell = field('xv_amount', {}, {
      customControl: { name: control.name, parameters: { value: { value: 'xv_amount', static: false }, other: { value: 'xv_missing', static: false } } },
    });
    mountProbe(control, { cells: [cell] });
    expect(await screen.findByText(/^Custom control 'xv_XvTest\.Two' could not be loaded: .*xv_missing/)).toBeInTheDocument();
    expect(probe.calls).not.toContain('init');
  });

  it('unmount_calls_destroy_once', async () => {
    const control = probeControl('Probe');
    mountProbe(control);
    await waitFor(() => expect(probe.notify).toBeDefined());
    cleanup();
    expect(count_('destroy')).toBe(1);
    expect(count_('init')).toBe(1);
  });

  it('context_members_outside_the_supported_set_throw_not_available', async () => {
    const control = probeControl('Probe');
    mountProbe(control);
    await waitFor(() => expect(probe.notify).toBeDefined());
    const ctx = last();
    expect(() => ctx.device).toThrow(/not available in this version/);
    expect(() => ctx.mode.setFullScreen(true)).toThrow(/not available in this version/);
    expect(() => ctx.factory.getPopupService()).toThrow(/not available in this version/);
    expect(ctx.client.getFormFactor()).toBe(1);
    expect(ctx.client.getClient()).toBe('Web');
    expect(ctx.client.isOffline()).toBe(false);
    expect(ctx.page.entityTypeName).toBe('xv_project');
    expect(ctx.page.entityId).toBe(RECORD_ID);
    expect(ctx.mode.contextInfo).toEqual({ entityId: RECORD_ID, entityTypeName: 'xv_project', entityRecordName: 'Alpha' });
    expect(ctx.fluentDesignLanguage.isDarkTheme).toBe(false);
  });

  it('context_info_is_empty_on_a_new_record', async () => {
    const control = probeControl('Probe');
    const ctx = contextOf([name, amount, count], undefined);
    mountForm(simpleForm([field('xv_name'), pcfField(control)]), ctx, { setup: () => serveControls([control]) });
    await waitFor(() => expect(probe.notify).toBeDefined());
    expect(last().mode.contextInfo).toEqual({ entityId: '', entityTypeName: 'xv_project', entityRecordName: '' });
  });

  it('standard_test_control_reads_its_strings_and_input_defaults', async () => {
    globals.__xvProbe = undefined;
    const ctx = contextOf([name, amount], RECORD);
    mountForm(simpleForm([pcfField(standardControl)]), ctx, { setup: () => serveControls([standardControl]) });
    // The Doubler writes the value into its container.
    expect(await screen.findByText('3')).toBeInTheDocument();
  });

  it('currency_formatted_uses_the_record_currency_symbol', async () => {
    const control = probeControl('Money', { ofType: 'Currency' });
    const ctx = contextOf([name, budget], RECORD);
    renderForm(simpleForm([pcfField(control, 'xv_budget')]), ctx, { formView: { currencySymbol: '€' }, setup: () => serveControls([control]) });
    await waitFor(() => expect(probe.notify).toBeDefined());
    expect(last().parameters.value.raw).toBe(1234.5);
    expect(last().parameters.value.formatted).toBe('€1,234.50');
    expect(last().parameters.value.attributes.Precision).toBe(2);
    expect(last().parameters.value.type).toBe('Currency');
  });
});
