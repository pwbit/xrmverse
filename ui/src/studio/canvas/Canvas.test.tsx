import { act, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { ITALIAN_LCID, setUiLanguage } from '../../shell/i18n/t';
import { it as itCatalogue } from '../../shell/i18n/it';
import { en } from '../../shell/i18n/en';
import type { EditorProblem } from '../editor/problems';
import { useEditorDocument } from '../editor/useEditorDocument';
import { phraseText } from '../model/phrase';
import { parseText } from '../model/read';
import type { StepPath } from '../model/types';
import { Canvas } from './Canvas';

const TEXT = [
  'kind: workflow', //                                            1
  'name: Flow',
  'entity: account',
  'mode: background',
  'trigger: { create: true, update: { columns: [name, revenue] } }', // 5
  'steps:',
  '  - update: { record: primary, set: { name: X } }', //          7
  '  - if:', //                                                    8
  '      when: { field: revenue, op: gt, value: 1000 }',
  '      then:',
  '        - create: { entity: task, set: { subject: Call } }', // 11
  '      elseIf:',
  '        - when: { field: revenue, op: gt, value: 100 }',
  '          then:',
  '            - stop: { status: canceled }', //                   15
  '      else:',
  '        - stop: { status: succeeded }', //                      17
  '  - wait:', //                                                  18
  '      until: { field: name, op: notNull }',
  '      timeout: { now: true }',
  '      then:',
  '        - update: { record: { related: parentaccountid }, set: { name: Y } }', // 22
  '      onTimeout:',
  '        - stop: { status: canceled }',
  '',
].join('\n');

/** The document's text after the last change (what a save would send). */
let current = '';

function Harness({ text, problems }: { text: string; problems?: Map<number, EditorProblem[]> }) {
  const doc = useEditorDocument(undefined, text);
  const [selected, setSelected] = useState<StepPath | 'trigger'>();
  current = doc.text;
  return doc.model ? (
    <Canvas model={doc.model} selected={selected} onSelect={setSelected} mutate={doc.mutate} problemsByLine={problems ?? new Map()} />
  ) : (
    <p>broken</p>
  );
}

const summaries = (text = current) => {
  const { model } = parseText(text);
  const walk = (steps: NonNullable<typeof model>['steps']): unknown[] =>
    steps.map((s) => (s.branches.length ? { [phraseText(s.summary)]: s.branches.map((b) => walk(b.steps)) } : phraseText(s.summary)));
  return walk(model!.steps);
};

const cards = () => Array.from(document.querySelectorAll<HTMLElement>('[data-card]'));
const card = (name: string | RegExp) => screen.getByRole('group', { name });
const menuOf = async (name: string | RegExp) => {
  await userEvent.click(within(card(name)).getAllByRole('button', { name: en['studio.canvas.cardMenu'] })[0]);
};
const slots = () => screen.getAllByRole('button', { name: en['studio.canvas.insert'] });

afterEach(() => setUiLanguage(undefined));

describe('Canvas', () => {
  it('draws the trigger card first, in our own words, and it cannot be removed', () => {
    render(<Harness text={TEXT} />);
    const first = cards()[0];
    expect(first).toBe(card(en['studio.canvas.trigger']));
    expect(first.textContent).toContain(en['studio.kind.workflow']);
    expect(first.textContent).toContain('account');
    expect(first.textContent).toContain(en['studio.mode.background']);
    expect(first.textContent).toContain(en['studio.canvas.trigger.create']);
    expect(first.textContent).toContain('name, revenue');
    expect(within(first).queryByRole('button', { name: en['studio.canvas.cardMenu'] })).toBeNull();
    expect(cards().map((c) => c.getAttribute('aria-label'))).toEqual([
      en['studio.canvas.trigger'],
      'Update record: primary',
      'If: revenue gt 1000',
      'Create record: task',
      'Stop: canceled',
      'Stop: succeeded',
      'Wait until name notNull or for a timeout',
      'Update record: related parentaccountid',
      'Stop: canceled',
    ]);
  });

  it('an if draws its then, else-if and else as labelled columns joined below', () => {
    render(<Harness text={TEXT} />);
    const ifCard = card('If: revenue gt 1000');
    const columns = within(ifCard).getAllByRole('group', { name: /^(Then|Else if 1|Else)$/ });
    expect(columns.map((c) => c.getAttribute('aria-label'))).toEqual([en['studio.canvas.branch.then'], 'Else if 1', en['studio.canvas.branch.else']]);
    expect(within(columns[0]).getByRole('group', { name: 'Create record: task' })).toBeInTheDocument();
    expect(within(columns[1]).getByRole('group', { name: 'Stop: canceled' })).toBeInTheDocument();
    expect(within(columns[2]).getByRole('group', { name: 'Stop: succeeded' })).toBeInTheDocument();
    expect(ifCard.querySelector('[data-join]')).not.toBeNull();
  });

  it('a wait draws its then and on-timeout columns', () => {
    render(<Harness text={TEXT} />);
    const waitCard = card(/^Wait until/);
    const columns = within(waitCard).getAllByRole('group', { name: /^(Then|On timeout)$/ });
    expect(columns.map((c) => c.getAttribute('aria-label'))).toEqual([en['studio.canvas.branch.then'], en['studio.canvas.branch.onTimeout']]);
    expect(waitCard.querySelector('[data-join]')).not.toBeNull();
  });

  it('"+" opens the palette of that place, with the missing steps disabled and their reasons', async () => {
    render(<Harness text={TEXT} />);
    await userEvent.click(slots()[0]);
    const palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
    expect(within(palette).getByRole('button', { name: en['studio.step.wait'] })).toBeEnabled();
    // Business rule actions are not offered in a workflow.
    expect(within(palette).queryByRole('button', { name: en['studio.step.setValue'] })).toBeNull();
    expect(palette.textContent).toContain(en['studio.palette.sendEmailFromTemplate']);
    expect(palette.textContent).toContain(en['studio.palette.sendEmailFromTemplate.reason']);
    expect(within(palette).queryByRole('button', { name: en['studio.palette.sendEmailFromTemplate'] })).toBeNull();
  });

  it('a real-time workflow offers no wait; a business rule offers its actions inside an if only', async () => {
    const rule = ['kind: businessRule', 'name: R', 'entity: account', 'steps:', '  - if:', '      when: { field: name, op: null }', '      then: []', ''].join('\n');
    render(<Harness text={rule} />);
    // The slots: root before the if, inside then, root end.
    await userEvent.click(slots()[0]);
    let palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
    expect(within(palette).queryByRole('button', { name: en['studio.step.setValue'] })).toBeNull();
    expect(within(palette).getByRole('button', { name: en['studio.step.if'] })).toBeEnabled();
    await userEvent.keyboard('{Escape}');
    await userEvent.click(within(card(/^If:/)).getByRole('button', { name: en['studio.canvas.insert'] }));
    palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
    expect(within(palette).getByRole('button', { name: en['studio.step.setValue'] })).toBeEnabled();
  });

  it('choosing a step inserts it at that slot', async () => {
    render(<Harness text={TEXT} />);
    await userEvent.click(slots()[0]);
    const palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
    await userEvent.click(within(palette).getByRole('button', { name: en['studio.step.update'] }));
    const steps = parseText(current).model!.steps;
    expect(steps).toHaveLength(4);
    expect(current).toContain('  - update:\n      record: primary\n      set: {}\n  - update: { record: primary, set: { name: X } }');
    // The new card is selected.
    expect(cards()[1]).toHaveAttribute('aria-current', 'true');
  });

  it('inserts at the end of a branch', async () => {
    render(<Harness text={TEXT} />);
    const elseColumn = within(card('If: revenue gt 1000')).getByRole('group', { name: en['studio.canvas.branch.else'] });
    const inElse = within(elseColumn).getAllByRole('button', { name: en['studio.canvas.insert'] });
    await userEvent.click(inElse[inElse.length - 1]);
    await userEvent.click(within(await screen.findByRole('dialog', { name: en['studio.canvas.palette'] })).getByRole('button', { name: en['studio.step.stop'] }));
    expect(summaries()[1]).toEqual({ 'If: revenue gt 1000': [['Create record: task'], ['Stop: canceled'], ['Stop: succeeded', 'Stop: succeeded']] });
  });

  it('card menu: Move up and Move down', async () => {
    render(<Harness text={TEXT} />);
    await menuOf('If: revenue gt 1000');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.moveDown'] }));
    expect(summaries().map((s) => (typeof s === 'string' ? s : Object.keys(s as object)[0]))).toEqual([
      'Update record: primary',
      'Wait until name notNull or for a timeout',
      'If: revenue gt 1000',
    ]);
    await menuOf('If: revenue gt 1000');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.moveUp'] }));
    await menuOf('If: revenue gt 1000');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.moveUp'] }));
    expect(summaries().map((s) => (typeof s === 'string' ? s : Object.keys(s as object)[0]))).toEqual([
      'If: revenue gt 1000',
      'Update record: primary',
      'Wait until name notNull or for a timeout',
    ]);
    // The first card cannot move up.
    await menuOf('If: revenue gt 1000');
    expect(await screen.findByRole('menuitem', { name: en['studio.canvas.moveUp'] })).toHaveAttribute('aria-disabled', 'true');
  });

  it('card menu: Cut, then Paste after another card', async () => {
    render(<Harness text={TEXT} />);
    await menuOf('Create record: task');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.cut'] }));
    expect(card('Create record: task')).toHaveAttribute('data-cut', 'true');
    await menuOf('Stop: succeeded');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.paste'] }));
    expect(summaries()[1]).toEqual({ 'If: revenue gt 1000': [[], ['Stop: canceled'], ['Stop: succeeded', 'Create record: task']] });
  });

  it('a cut card can be pasted from a slot', async () => {
    render(<Harness text={TEXT} />);
    await menuOf('Update record: primary');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.cut'] }));
    const all = slots();
    await userEvent.click(all[all.length - 1]);
    const palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
    await userEvent.click(within(palette).getByRole('button', { name: /^Paste/ }));
    expect(summaries().map((s) => (typeof s === 'string' ? s : Object.keys(s as object)[0]))).toEqual([
      'If: revenue gt 1000',
      'Wait until name notNull or for a timeout',
      'Update record: primary',
    ]);
  });

  it('Delete removes a card at once, and asks first when the card has children', async () => {
    render(<Harness text={TEXT} />);
    await menuOf('Update record: primary');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.delete'] }));
    expect(summaries()).toHaveLength(2);
    await menuOf('If: revenue gt 1000');
    await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.delete'] }));
    const dialog = await screen.findByRole('dialog', { name: en['studio.canvas.delete.title'] });
    expect(summaries()).toHaveLength(2);
    await userEvent.click(within(dialog).getByRole('button', { name: en['studio.canvas.delete.confirm'] }));
    expect(summaries().map((s) => Object.keys(s as object)[0])).toEqual(['Wait until name notNull or for a timeout']);
  });

  it('dragging a card onto a slot moves it', () => {
    render(<Harness text={TEXT} />);
    const data: Record<string, string> = {};
    const dataTransfer = { setData: (k: string, v: string) => (data[k] = v), getData: (k: string) => data[k] ?? '', effectAllowed: '', dropEffect: '' };
    const elseColumn = within(card('If: revenue gt 1000')).getByRole('group', { name: en['studio.canvas.branch.else'] });
    const inElse = within(elseColumn).getAllByRole('button', { name: en['studio.canvas.insert'] });
    const target = inElse[inElse.length - 1].closest('[data-slot]')!;
    fireEvent.dragStart(card('Update record: primary'), { dataTransfer });
    const accepted = !fireEvent.dragOver(target, { dataTransfer });
    expect(accepted).toBe(true);
    fireEvent.drop(target, { dataTransfer });
    fireEvent.dragEnd(card('Update record: primary'), { dataTransfer });
    expect(summaries()[0]).toEqual({ 'If: revenue gt 1000': [['Create record: task'], ['Stop: canceled'], ['Stop: succeeded', 'Update record: primary']] });
  });

  it('a card cannot be dropped into its own branches', () => {
    render(<Harness text={TEXT} />);
    const data: Record<string, string> = {};
    const dataTransfer = { setData: (k: string, v: string) => (data[k] = v), getData: (k: string) => data[k] ?? '', effectAllowed: '', dropEffect: '' };
    const elseColumn = within(card('If: revenue gt 1000')).getByRole('group', { name: en['studio.canvas.branch.else'] });
    const target = within(elseColumn).getAllByRole('button', { name: en['studio.canvas.insert'] })[0].closest('[data-slot]')!;
    const before = current;
    fireEvent.dragStart(card('If: revenue gt 1000'), { dataTransfer });
    expect(!fireEvent.dragOver(target, { dataTransfer })).toBe(false);
    fireEvent.drop(target, { dataTransfer });
    expect(current).toBe(before);
  });

  describe('moves follow the place rule (a business rule action stays inside its if)', () => {
    const RULE = [
      'kind: businessRule',
      'name: R',
      'entity: account',
      'steps:',
      '  - if:',
      '      when: { field: name, op: null }',
      '      then:',
      '        - setValue: { field: name, value: X }',
      '      else: []',
      '',
    ].join('\n');
    const transfer = () => {
      const data: Record<string, string> = {};
      return { setData: (k: string, v: string) => (data[k] = v), getData: (k: string) => data[k] ?? '', effectAllowed: '', dropEffect: '' };
    };
    const actionCard = () => card(/^Set value/);

    it('a rule action cannot be dropped on a top-level slot, and the document is unchanged', () => {
      render(<Harness text={RULE} />);
      const dataTransfer = transfer();
      const top = slots()[0].closest('[data-slot]')!;
      const before = current;
      fireEvent.dragStart(actionCard(), { dataTransfer });
      expect(!fireEvent.dragOver(top, { dataTransfer })).toBe(false);
      fireEvent.drop(top, { dataTransfer });
      fireEvent.dragEnd(actionCard(), { dataTransfer });
      expect(current).toBe(before);
      expect(parseText(current).problems).toEqual([]);
    });

    it('a rule action can still be dropped into another branch of the if', () => {
      render(<Harness text={RULE} />);
      const dataTransfer = transfer();
      const elseColumn = within(card(/^If:/)).getByRole('group', { name: en['studio.canvas.branch.else'] });
      const target = within(elseColumn).getAllByRole('button', { name: en['studio.canvas.insert'] })[0].closest('[data-slot]')!;
      fireEvent.dragStart(actionCard(), { dataTransfer });
      expect(!fireEvent.dragOver(target, { dataTransfer })).toBe(true);
      fireEvent.drop(target, { dataTransfer });
      const { model, problems } = parseText(current);
      expect(problems).toEqual([]);
      expect(model!.steps[0].branches.map((b) => [b.label, b.steps.map((step) => step.type)])).toEqual([
        ['then', []],
        ['else', ['setValue']],
      ]);
    });

    it('after Cut, Paste is disabled at a top-level slot with the reason, and offered inside the if', async () => {
      render(<Harness text={RULE} />);
      await menuOf(/^Set value/);
      await userEvent.click(await screen.findByRole('menuitem', { name: en['studio.canvas.cut'] }));
      // Paste after the if (a top-level place) is refused.
      await menuOf(/^If:/);
      expect(await screen.findByRole('menuitem', { name: en['studio.canvas.paste'] })).toHaveAttribute('aria-disabled', 'true');
      await userEvent.keyboard('{Escape}');
      await userEvent.click(slots()[0]);
      let palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
      const before = current;
      const paste = within(palette).getByRole('button', { name: /^Paste/ });
      expect(paste).toHaveAttribute('aria-disabled', 'true');
      expect(palette.textContent).toContain(en['studio.canvas.moveNotHere']);
      await userEvent.click(paste);
      expect(current).toBe(before);
      await userEvent.keyboard('{Escape}');
      // Inside the else branch it is offered and works.
      const elseColumn = within(card(/^If:/)).getByRole('group', { name: en['studio.canvas.branch.else'] });
      await userEvent.click(within(elseColumn).getAllByRole('button', { name: en['studio.canvas.insert'] })[0]);
      palette = await screen.findByRole('dialog', { name: en['studio.canvas.palette'] });
      await userEvent.click(within(palette).getByRole('button', { name: /^Paste/ }));
      expect(parseText(current).problems).toEqual([]);
      expect(parseText(current).model!.steps[0].branches[1].steps.map((step) => step.type)).toEqual(['setValue']);
    });
  });

  it('keyboard: every card and slot is in the Tab order, in the order of the flow; Enter selects a card and opens a slot', async () => {
    render(<Harness text={TEXT} />);
    // In jsdom Fluent's focus manager (tabster) sees every element as invisible and swallows Tab, so the Tab order is read
    // from the document: the elements a browser tabs through, in document order.
    const order = Array.from(document.querySelectorAll<HTMLElement>('button, [tabindex]')).filter(
      (el) => el.tabIndex >= 0 && !(el as HTMLButtonElement).disabled && !el.closest('[aria-hidden="true"]'),
    );
    const cardsAndSlots = order.filter((el) => el.hasAttribute('data-card') || el.getAttribute('aria-label') === en['studio.canvas.insert']);
    expect(new Set(cardsAndSlots)).toEqual(new Set([...cards(), ...slots()]));
    const names = cardsAndSlots.map((el) => (el.hasAttribute('data-card') ? el.getAttribute('aria-label') : '+'));
    expect(names.slice(0, 8)).toEqual([en['studio.canvas.trigger'], '+', 'Update record: primary', '+', 'If: revenue gt 1000', '+', 'Create record: task', '+']);

    act(() => card('If: revenue gt 1000').focus());
    await userEvent.keyboard('{Enter}');
    expect(card('If: revenue gt 1000')).toHaveAttribute('aria-current', 'true');
    act(() => cards()[0].focus());
    await userEvent.keyboard('{Enter}');
    expect(cards()[0]).toHaveAttribute('aria-current', 'true');
    expect(card('If: revenue gt 1000')).not.toHaveAttribute('aria-current');
    act(() => slots()[0].focus());
    await userEvent.keyboard('{Enter}');
    expect(await screen.findByRole('dialog', { name: en['studio.canvas.palette'] })).toBeInTheDocument();
  });

  it('a card whose lines hold a problem shows an error badge', () => {
    const problem = (line: number): EditorProblem => ({ severity: 'error', message: 'bad', line, column: 1, source: 'server' });
    render(<Harness text={TEXT} problems={new Map([[15, [problem(15)]], [5, [problem(5)]]])} />);
    const badge = (name: string | RegExp) => within(card(name)).queryAllByLabelText(/problem/);
    // The deepest card that holds the line has the badge; the if around it does not.
    expect(badge('Stop: succeeded')).toHaveLength(0);
    const stops = screen.getAllByRole('group', { name: 'Stop: canceled' });
    expect(within(stops[0]).getByLabelText('1 problem')).toBeInTheDocument();
    expect(within(stops[1]).queryByLabelText(/problem/)).toBeNull();
    expect(within(cards()[0]).getByLabelText('1 problem')).toBeInTheDocument();
    for (const update of screen.getAllByRole('group', { name: 'Update record: primary' })) expect(within(update).queryByLabelText(/problem/)).toBeNull();
  });

  it('adds else-if and else branches to an if, and on-timeout to a wait with a timeout only', async () => {
    const text = [
      'kind: workflow',
      'name: F',
      'entity: account',
      'mode: background',
      'steps:',
      '  - if:',
      '      when: { field: name, op: null }',
      '      then: []',
      '  - wait:',
      '      until: { field: name, op: notNull }',
      '      then: []',
      '',
    ].join('\n');
    render(<Harness text={text} />);
    await userEvent.click(within(card(/^If:/)).getByRole('button', { name: en['studio.canvas.addElseIf'] }));
    await userEvent.click(within(card(/^If:/)).getByRole('button', { name: en['studio.canvas.addElse'] }));
    expect(within(card(/^If:/)).getAllByRole('group', { name: /^(Then|Else if 1|Else)$/ })).toHaveLength(3);
    // There is an else now.
    expect(within(card(/^If:/)).queryByRole('button', { name: en['studio.canvas.addElse'] })).toBeNull();
    await userEvent.click(within(card(/^Wait/)).getByRole('button', { name: en['studio.canvas.addOnTimeout'] }));
    expect(await screen.findByText(en['studio.canvas.onTimeoutNeedsTimeout'])).toBeInTheDocument();
    expect(current).not.toContain('onTimeout');
  });

  it('without mutate it is a read-only diagram: no slots, menus, branch buttons, drag or selection', async () => {
    const selections: unknown[] = [];
    render(<Canvas model={parseText(TEXT).model!} onSelect={(at) => selections.push(at)} problemsByLine={new Map()} />);
    expect(card('Create record: task')).toBeInTheDocument();
    expect(screen.queryAllByRole('button', { name: en['studio.canvas.insert'] })).toEqual([]);
    expect(screen.queryAllByRole('button', { name: en['studio.canvas.cardMenu'] })).toEqual([]);
    expect(screen.queryByRole('button', { name: en['studio.canvas.addElse'] })).toBeNull();
    expect(screen.queryByRole('button', { name: en['studio.canvas.addElseIf'] })).toBeNull();
    const update = card('Update record: primary');
    expect(update).not.toHaveAttribute('draggable', 'true');
    expect(update).not.toHaveAttribute('tabindex');
    await userEvent.click(update);
    await userEvent.click(card(en['studio.canvas.trigger']));
    expect(selections).toEqual([]);
    expect(update).not.toHaveAttribute('aria-current');
    // Zoom still works: it changes the view, not the document.
    await userEvent.click(screen.getByRole('button', { name: en['studio.canvas.zoomOut'] }));
    expect(screen.getByText('Zoom 90 %')).toBeInTheDocument();
  });

  it('cards and branch conditions read in the UI language', () => {
    setUiLanguage(ITALIAN_LCID);
    render(<Harness text={TEXT} />);
    const ifCard = card('Se: revenue gt 1000');
    expect(ifCard.textContent).toContain('Se: revenue gt 1000');
    const then = within(ifCard).getByRole('group', { name: itCatalogue['studio.canvas.branch.then'] });
    expect(then.textContent).toContain('revenue gt 1000');
    expect(card('Aggiorna record: primary')).toBeInTheDocument();
    expect(card(/^Attendi finché name notNull o per un timeout$/)).toBeInTheDocument();
  });
});
