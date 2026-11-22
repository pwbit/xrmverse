import { act, renderHook, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { setLibraryLoader } from '../../client-api/scriptHost';
import { sourceLoader } from '../../client-api/testHost';
import type { FormNotification } from '../../renderers/classic/form/FormNotifications';
import { server } from '../../test/server';
import type { RecordForm } from './useRecordForm';
import { useRibbonCommands, type RibbonCommandsInput } from './useRibbonCommands';

/** Lets a test make the parser throw something the hook does not expect (a bug, not a diff it can warn about). */
const parserFault = vi.hoisted(() => ({ error: undefined as Error | undefined }));
vi.mock('../../parsers/ribbonDiffXml', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../parsers/ribbonDiffXml')>();
  return {
    ...actual,
    formCommands: (...args: Parameters<typeof actual.formCommands>) => {
      if (parserFault.error) throw parserFault.error;
      return actual.formCommands(...args);
    },
  };
});

const ENTITY = 'xv_project';
const LOC = `Mscrm.Form.${ENTITY}.MainTab.Actions.Controls._children`;
const LIB = 'xv_/project.js';
const RECORD = 'a1b2c3d4-0000-0000-0000-000000000001';

/** A button `id` whose command shows on the display rule `display` and is enabled by the enable rule `enable` (written by us). */
const button = (id: string, label: string, sequence: number, display: string, enable = '', action = `<JavaScriptFunction Library="$webresource:${LIB}" FunctionName="XV.${id}"><CrmParameter Value="PrimaryControl" /></JavaScriptFunction>`) => ({
  action: `<CustomAction Id="xv.${id}.CA" Location="${LOC}" Sequence="${sequence}"><CommandUIDefinition><Button Id="xv.${id}.B" Command="xv.${id}.C" LabelText="${label}" ModernImage="Accept" /></CommandUIDefinition></CustomAction>`,
  command: `<CommandDefinition Id="xv.${id}.C"><EnableRules>${enable ? `<EnableRule Id="${enable}" />` : ''}</EnableRules><DisplayRules><DisplayRule Id="${display}" /></DisplayRules><Actions>${action}</Actions></CommandDefinition>`,
});

const RULES = `<RuleDefinitions>
  <DisplayRules>
    <DisplayRule Id="xv.Existing"><FormStateRule State="Existing" /></DisplayRule>
    <DisplayRule Id="xv.Create"><FormStateRule State="Create" /></DisplayRule>
  </DisplayRules>
  <EnableRules>
    <EnableRule Id="xv.IsDraft"><ValueRule Field="statuscode" Value="1" /></EnableRule>
    <EnableRule Id="xv.Custom"><CustomRule Library="$webresource:${LIB}" FunctionName="XV.rule" Default="false" /></EnableRule>
    <EnableRule Id="xv.Selection"><SelectionCountRule Minimum="1" /></EnableRule>
  </EnableRules>
</RuleDefinitions>`;

function diff(buttons: ReturnType<typeof button>[], extra = ''): string {
  return `<RibbonDiffXml><CustomActions>${buttons.map((b) => b.action).join('')}${extra}</CustomActions><CommandDefinitions>${buttons.map((b) => b.command).join('')}</CommandDefinitions>${RULES}</RibbonDiffXml>`;
}

const APPROVE = button('approve', 'Approve', 10, 'xv.Existing', 'xv.IsDraft');
const START = button('start', 'Start', 20, 'xv.Create');
const CHECKED = button('checked', 'Checked', 30, 'xv.Existing', 'xv.Custom');
const BROKEN = button('broken', 'Broken', 40, 'xv.Existing', 'xv.Selection');

function serve(xml: string | string[], status = 200) {
  const requests: string[] = [];
  server.use(
    http.get(`*/Ribbon/${ENTITY}`, ({ request }) => {
      requests.push(request.url);
      if (status !== 200) return HttpResponse.json({ title: 'Forbidden', status, detail: 'No read access to solution.' }, { status });
      return HttpResponse.json({ entity: ENTITY, diffs: (Array.isArray(xml) ? xml : [xml]).map((x) => ({ solution: 'XvProject', xml: x })) });
    }),
  );
  return requests;
}

interface Probe {
  calls: unknown[];
  rules: number;
  answer: boolean | 'pending';
  /** Resolvers of the CustomRule calls answered with a pending promise. */
  pending: ((v: boolean) => void)[];
  release?: () => void;
  reject?: (e: Error) => void;
  fail?: string;
  /** The record id the fake form reports; an action may change it. */
  holder: { id?: string };
  ids: unknown[];
}
const probe = (): Probe => (window as unknown as { __probe: Probe }).__probe;

const SOURCE = `var P = window.__probe; var XV = {
  approve: function (fc) { P.calls.push(fc); return new Promise(function (r, j) { P.release = r; P.reject = j; }); },
  create: function () { P.holder.id = 'c0ffee00-0000-0000-0000-000000000001'; },
  report: function (id) { P.ids.push(id); },
  start: function (fc) { P.calls.push(fc); if (P.fail) throw new Error(P.fail); },
  checked: function (fc) { P.calls.push(fc); },
  rule: function () { P.rules++; return P.answer === 'pending' ? new Promise(function (r) { P.pending.push(r); }) : P.answer; },
};`;

/** A formContext with the attributes in `attrs` (value by logical name), no controls. */
function fakeContext(attrs: Record<string, unknown>) {
  return {
    getAttribute: (name: string) => (name in attrs ? { getValue: () => attrs[name] } : null),
    ui: { controls: { get: () => [] } },
  };
}

function fakeForm(over: { id?: string; values?: Record<string, unknown>; busy?: boolean; isBusy?: boolean; readOnly?: boolean; holder?: { id?: string } } = {}): RecordForm {
  const values = over.values ?? {};
  const holder = over.holder ?? { id: 'id' in over ? over.id : RECORD };
  return {
    values,
    original: values,
    latest: () => ({ values, original: values, id: holder.id }),
    record: holder.id ? { id: holder.id } : undefined,
    dirty: false,
    busy: over.busy ?? false,
    isBusy: () => over.isBusy ?? over.busy ?? false,
    readOnly: over.readOnly ?? false,
  } as unknown as RecordForm;
}

function input(over: Partial<RibbonCommandsInput> & { notes?: FormNotification[] } = {}): RibbonCommandsInput {
  const notes = over.notes ?? [];
  return {
    entity: ENTITY,
    lcid: 1033,
    form: fakeForm(),
    formContext: fakeContext({ statuscode: 1 }),
    formId: '{F0000000-0000-0000-0000-000000000001}',
    refreshKey: 0,
    // Like the overlay: a notification replaces the one with its key; clear removes it.
    notify: (n) => {
      const at = notes.findIndex((x) => x.key === n.key);
      if (at < 0) notes.push(n);
      else notes[at] = n;
    },
    clear: (key) => {
      const at = notes.findIndex((x) => x.key === key);
      if (at >= 0) notes.splice(at, 1);
    },
    // The test's probe reaches the libraries the way Xrm does: put on the host window before they load.
    install: (win) => ((win as unknown as { __probe: Probe }).__probe = probe()),
    ...over,
  };
}

const labels = (r: { current: ReturnType<typeof useRibbonCommands> }) => r.current.visible.map((v) => v.command.label);

beforeEach(() => {
  (window as unknown as { __probe: Probe }).__probe = { calls: [], rules: 0, answer: true, pending: [], holder: {}, ids: [] };
  setLibraryLoader(sourceLoader({ [LIB]: SOURCE }));
});
afterEach(() => setLibraryLoader(undefined));

describe('useRibbonCommands', () => {
  it('shows_a_button_when_its_rules_pass_and_hides_it_when_a_value_changes', async () => {
    serve(diff([APPROVE]));
    const attrs: Record<string, unknown> = { statuscode: 1 };
    const fc = fakeContext(attrs);
    const { result, rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ formContext: fc, form: fakeForm({ values: { statuscode: 1 } }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    expect(result.current.visible[0]!.command.icon).toBe('Accept');
    attrs.statuscode = 2;
    rerender(input({ formContext: fc, form: fakeForm({ values: { statuscode: 2 } }) }));
    await waitFor(() => expect(labels(result)).toEqual([]));
  });

  it('a_click_calls_the_function_with_the_form_context_and_the_button_is_busy_while_it_runs', async () => {
    serve(diff([APPROVE]));
    const fc = fakeContext({ statuscode: 1 });
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ formContext: fc }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(probe().calls).toHaveLength(1));
    expect(probe().calls[0]).toBe(fc);
    expect(result.current.visible[0]!.busy).toBe(true);
    // A second click while the first one runs is ignored.
    act(() => result.current.visible[0]!.run());
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(probe().calls).toHaveLength(1);
    await act(async () => probe().release!());
    await waitFor(() => expect(result.current.visible[0]!.busy).toBe(false));
    expect(probe().calls).toHaveLength(1);
  });

  it('a_button_is_busy_and_ignores_clicks_while_the_form_saves', async () => {
    serve(diff([APPROVE]));
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ form: fakeForm({ busy: true }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    expect(result.current.visible[0]!.busy).toBe(true);
    act(() => result.current.visible[0]!.run());
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(probe().calls).toHaveLength(0);
  });

  it('an_action_error_becomes_an_error_notification', async () => {
    serve(diff([START]));
    probe().fail = 'boom';
    const notes: FormNotification[] = [];
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes, form: fakeForm({ id: undefined }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Start']));
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(notes.some((n) => n.intent === 'error')).toBe(true));
    expect(notes.find((n) => n.intent === 'error')!.text).toBe('Script error in XV.start: boom');
    await waitFor(() => expect(result.current.visible[0]!.busy).toBe(false));
  });

  it('an_unavailable_command_gives_one_warning_and_no_button', async () => {
    serve(diff([APPROVE, BROKEN], `<HideCustomAction HideActionId="xv.hide" Location="Mscrm.Form.${ENTITY}.Deactivate" />`));
    const notes: FormNotification[] = [];
    const { result, rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    rerender(input({ notes, form: fakeForm({ values: { statuscode: 1 } }) }));
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    const warnings = notes.filter((n) => n.intent === 'warning').map((n) => n.text);
    expect(warnings.filter((w) => w.startsWith("Command 'Broken'"))).toEqual(["Command 'Broken' is not available: rule 'SelectionCountRule' is not supported in this version"]);
    expect(warnings).toContain(`Hiding 'Mscrm.Form.${ENTITY}.Deactivate' is not supported in this version`);
  });

  it('an_unknown_crm_parameter_makes_the_command_unavailable', async () => {
    const odd = button('odd', 'Odd', 50, 'xv.Existing', '', `<JavaScriptFunction Library="$webresource:${LIB}" FunctionName="XV.checked"><CrmParameter Value="SelectedControl" /></JavaScriptFunction>`);
    serve(diff([APPROVE, odd]));
    const notes: FormNotification[] = [];
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    expect(notes.map((n) => n.text)).toContain("Command 'Odd' is not available: CrmParameter 'SelectedControl' is not supported in this version");
  });

  it('a_library_that_fails_to_load_makes_its_commands_unavailable', async () => {
    setLibraryLoader(sourceLoader({}));
    serve(diff([APPROVE, CHECKED]));
    const notes: FormNotification[] = [];
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(notes.filter((n) => n.text.startsWith('Command ')).length).toBe(2));
    expect(labels(result)).toEqual([]);
    expect(notes.map((n) => n.text)).toContain(`Command 'Approve' is not available: the library ${LIB} could not be loaded`);
  });

  it('a_403_gives_the_E1_warning_and_no_commands', async () => {
    serve('', 403);
    const notes: FormNotification[] = [];
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(notes).toHaveLength(1));
    expect(notes[0]).toMatchObject({ intent: 'warning', text: 'Custom commands could not be read: No read access to solution.' });
    expect(result.current.visible).toEqual([]);
  });

  it('an_unexpected_error_while_building_the_commands_gives_the_ribbon_read_warning_and_no_commands', async () => {
    serve(diff([APPROVE]));
    parserFault.error = new Error('boom');
    const unhandled = vi.fn();
    process.on('unhandledRejection', unhandled);
    const notes: FormNotification[] = [];
    try {
      const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
      await waitFor(() => expect(notes).toHaveLength(1));
      expect(notes[0]).toMatchObject({ intent: 'warning', text: 'Custom commands could not be read: boom' });
      expect(result.current.visible).toEqual([]);
      await new Promise((r) => setTimeout(r, 20));
      expect(unhandled).not.toHaveBeenCalled();
    } finally {
      parserFault.error = undefined;
      process.off('unhandledRejection', unhandled);
    }
  });

  it('refresh_evaluates_the_rules_again', async () => {
    serve(diff([CHECKED]));
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input() });
    await waitFor(() => expect(labels(result)).toEqual(['Checked']));
    const before = probe().rules;
    probe().answer = false;
    act(() => result.current.refresh());
    await waitFor(() => expect(labels(result)).toEqual([]));
    expect(probe().rules).toBe(before + 1);
  });

  it('a_new_record_shows_the_create_commands_only', async () => {
    serve(diff([APPROVE, START]));
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ form: fakeForm({ id: undefined }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Start']));
  });

  it('fetches_once_per_load_and_again_on_refresh_key', async () => {
    const requests = serve(diff([APPROVE]));
    const { result, rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input() });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    rerender(input({ form: fakeForm({ values: { statuscode: 1 } }) }));
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    expect(requests).toHaveLength(1);
    rerender(input({ refreshKey: 1 }));
    await waitFor(() => expect(requests).toHaveLength(2));
  });

  it('one_ribbon_host_per_form_disposed_on_unmount', async () => {
    serve(diff([APPROVE, CHECKED]));
    const loads = vi.fn(sourceLoader({ [LIB]: SOURCE }));
    setLibraryLoader(loads);
    const { result, unmount } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input() });
    await waitFor(() => expect(labels(result)).toEqual(['Approve', 'Checked']));
    act(() => result.current.refresh());
    act(() => result.current.visible[1]!.run());
    await waitFor(() => expect(probe().calls).toHaveLength(1));
    expect(loads).toHaveBeenCalledTimes(1);
    expect(document.querySelectorAll('iframe')).toHaveLength(1);
    unmount();
    expect(document.querySelectorAll('iframe')).toHaveLength(0);
  });

  it('a_refresh_clears_the_warnings_of_the_previous_load', async () => {
    serve('', 403);
    const notes: FormNotification[] = [];
    const { rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(notes.map((n) => n.text)).toEqual(['Custom commands could not be read: No read access to solution.']));
    serve(diff([APPROVE]));
    rerender(input({ notes, refreshKey: 1 }));
    await waitFor(() => expect(notes).toEqual([]));
  });

  it('a_refresh_clears_parser_and_unavailable_warnings_keyed_by_text', async () => {
    const hide = `<HideCustomAction HideActionId="xv.hide" Location="Mscrm.Form.${ENTITY}.Deactivate" />`;
    serve(diff([APPROVE, BROKEN], hide + hide.replace('xv.hide', 'xv.hide2').replace('Deactivate', 'Assign')));
    const notes: FormNotification[] = [];
    const { result, rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(notes).toHaveLength(3));
    expect(notes.map((n) => n.key)).toContain(`ribbon-warning:Hiding 'Mscrm.Form.${ENTITY}.Assign' is not supported in this version`);
    serve(diff([APPROVE]));
    rerender(input({ notes, refreshKey: 1 }));
    await waitFor(() => expect(notes).toEqual([]));
    expect(labels(result)).toEqual(['Approve']);
  });

  it('the_last_evaluation_wins', async () => {
    serve(diff([CHECKED]));
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input() });
    await waitFor(() => expect(labels(result)).toEqual(['Checked']));
    // An older evaluation is still waiting for its CustomRule ...
    probe().answer = 'pending';
    act(() => result.current.refresh());
    await waitFor(() => expect(probe().pending).toHaveLength(1));
    // ... when a newer one answers false.
    probe().answer = false;
    act(() => result.current.refresh());
    await waitFor(() => expect(labels(result)).toEqual([]));
    await act(async () => probe().pending[0]!(true));
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(labels(result)).toEqual([]);
  });

  it('a_hanging_custom_rule_does_not_hold_back_other_buttons', async () => {
    serve(diff([APPROVE, CHECKED]));
    probe().answer = 'pending';
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input() });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    expect(probe().pending).toHaveLength(1);
    await act(async () => probe().pending[0]!(true));
    await waitFor(() => expect(labels(result)).toEqual(['Approve', 'Checked']));
  });

  it('nothing_is_shown_after_unmount_during_an_action', async () => {
    serve(diff([APPROVE]));
    const notes: FormNotification[] = [];
    const notify = vi.fn();
    const { result, unmount } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes, notify }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(probe().calls).toHaveLength(1));
    const errors = vi.spyOn(console, 'error');
    unmount();
    await act(async () => probe().reject!(new Error('late')));
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(notify).not.toHaveBeenCalled();
    expect(errors).not.toHaveBeenCalled();
    errors.mockRestore();
  });

  it('a_click_while_a_save_starts_does_nothing', async () => {
    serve(diff([APPROVE]));
    let saving = false;
    // The render still says not busy; the form's live flag and the page's save say otherwise.
    const { result, rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ form: fakeForm({ busy: false, isBusy: true }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    expect(result.current.visible[0]!.busy).toBe(false);
    act(() => result.current.visible[0]!.run());
    const form = fakeForm();
    rerender(input({ form, saving: () => saving }));
    saving = true;
    act(() => result.current.visible[0]!.run());
    await act(async () => {
      await new Promise((r) => setTimeout(r, 20));
    });
    expect(probe().calls).toHaveLength(0);
    saving = false;
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(probe().calls).toHaveLength(1));
  });

  it('a_library_added_by_a_refresh_asks_to_open_the_form_again', async () => {
    const LIB2 = 'xv_/later.js';
    const later = button('later', 'Later', 60, 'xv.Existing', '', `<JavaScriptFunction Library="$webresource:${LIB2}" FunctionName="XV.checked" />`);
    serve(diff([APPROVE]));
    const notes: FormNotification[] = [];
    const { result, rerender } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes }) });
    await waitFor(() => expect(labels(result)).toEqual(['Approve']));
    serve(diff([APPROVE, later]));
    rerender(input({ notes, refreshKey: 1 }));
    await waitFor(() => expect(notes.map((n) => n.text)).toEqual(["Command 'Later' is not available: its library was added after the form opened; open the form again"]));
    expect(labels(result)).toEqual(['Approve']);
  });

  it('each_action_reads_the_form_as_the_previous_one_left_it', async () => {
    const actions = `<JavaScriptFunction Library="$webresource:${LIB}" FunctionName="XV.create" /><JavaScriptFunction Library="$webresource:${LIB}" FunctionName="XV.report"><CrmParameter Value="FirstPrimaryItemId" /></JavaScriptFunction>`;
    serve(diff([button('create', 'Create it', 70, 'xv.Create', '', actions)]));
    const holder = probe().holder;
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ form: fakeForm({ holder }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Create it']));
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(probe().ids).toEqual(['{C0FFEE00-0000-0000-0000-000000000001}']));
  });

  it('a_new_run_clears_the_error_of_the_previous_one', async () => {
    serve(diff([START]));
    probe().fail = 'boom';
    const notes: FormNotification[] = [];
    const { result } = renderHook((p: RibbonCommandsInput) => useRibbonCommands(p), { initialProps: input({ notes, form: fakeForm({ id: undefined }) }) });
    await waitFor(() => expect(labels(result)).toEqual(['Start']));
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(notes.map((n) => n.intent)).toEqual(['error']));
    await waitFor(() => expect(result.current.visible[0]!.busy).toBe(false));
    probe().fail = undefined;
    act(() => result.current.visible[0]!.run());
    await waitFor(() => expect(notes).toEqual([]));
  });
});
