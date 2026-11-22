import type { RibbonAction, RibbonCommand, RibbonParameter, RibbonRule } from '../model/ribbon';
import { childElements, parseXml } from './xml';

/** Raised while reading a command; its message is the `unavailable` reason. */
class Unavailable extends Error {}

const WEB_RESOURCE_PREFIX = '$webresource:';
const LOC_LABEL_PREFIX = '$LocLabels:';

const flag = (el: Element, name: string): boolean => el.getAttribute(name)?.trim().toLowerCase() === 'true';
const attr = (el: Element, name: string): string => el.getAttribute(name) ?? '';

const PARAMETER_KINDS: Record<string, 'crm' | 'string' | 'bool' | 'int' | 'decimal'> = {
  CrmParameter: 'crm',
  StringParameter: 'string',
  BoolParameter: 'bool',
  IntParameter: 'int',
  DecimalParameter: 'decimal',
};

function parametersOf(el: Element): RibbonParameter[] {
  return Array.from(el.children).map((p) => {
    const kind = PARAMETER_KINDS[p.tagName];
    if (!kind) throw new Unavailable(`parameter '${p.tagName}' is not supported in this version`);
    return { kind, value: attr(p, 'Value') };
  });
}

function libraryOf(el: Element): string {
  const library = attr(el, 'Library').trim();
  if (!library.startsWith(WEB_RESOURCE_PREFIX)) {
    throw new Unavailable(`library '${library}' is not a web resource ('${WEB_RESOURCE_PREFIX}...')`);
  }
  return library.slice(WEB_RESOURCE_PREFIX.length);
}

const FORM_STATES = ['Create', 'Existing', 'ReadOnly', 'Disabled', 'BulkEdit'];

function ruleOf(el: Element): RibbonRule {
  const invert = flag(el, 'InvertResult');
  const dflt = flag(el, 'Default');
  switch (el.tagName) {
    case 'FormStateRule':
      if (!FORM_STATES.includes(attr(el, 'State'))) throw new Unavailable(`form state '${attr(el, 'State')}' is not supported in this version`);
      return { kind: 'formState', state: attr(el, 'State'), invert, dflt };
    case 'ValueRule':
      return { kind: 'value', field: attr(el, 'Field'), value: attr(el, 'Value'), invert, dflt };
    case 'OrRule':
      return { kind: 'or', groups: childElements(el, 'Or').map((g) => Array.from(g.children).map(ruleOf)), invert };
    case 'EntityRule': {
      const context = el.getAttribute('Context');
      return { kind: 'entity', entity: attr(el, 'EntityName'), ...(context ? { context } : {}), invert };
    }
    case 'CrmClientTypeRule':
      return { kind: 'clientType', type: attr(el, 'Type'), invert };
    case 'CustomRule':
      return { kind: 'custom', library: libraryOf(el), functionName: attr(el, 'FunctionName'), parameters: parametersOf(el), invert, dflt };
    default:
      throw new Unavailable(`rule '${el.tagName}' is not supported in this version`);
  }
}

/** Every `<tag>` child of every `<holder>` child of `parent`. */
const nested = (parent: Element, holder: string, tag: string): Element[] =>
  childElements(parent, holder).flatMap((h) => childElements(h, tag));

/** Titles of a LocLabel in document order, as lcid to text. */
interface LocTitles {
  byLcid: Map<number, string>;
  first?: string;
}

function locTitles(el: Element): LocTitles {
  const out: LocTitles = { byLcid: new Map() };
  for (const t of nested(el, 'Titles', 'Title')) {
    const text = t.getAttribute('description');
    if (text === null) continue;
    out.first ??= text;
    const lcid = t.getAttribute('languagecode')?.trim() ?? '';
    if (/^\d+$/.test(lcid)) out.byLcid.set(Number(lcid), text);
  }
  return out;
}

/** `$LocLabels:{id}` resolves to `lcid`, then 1033, then the first Title, else the id; any other text is used as is. */
function resolve(text: string, labels: Map<string, LocTitles>, lcid: number): string {
  if (!text.startsWith(LOC_LABEL_PREFIX)) return text;
  const id = text.slice(LOC_LABEL_PREFIX.length);
  const titles = labels.get(id);
  return titles?.byLcid.get(lcid) ?? titles?.byLcid.get(1033) ?? titles?.first ?? id;
}

interface Merged {
  actions: Map<string, Element>;
  commands: Map<string, Element>;
  display: Map<string, Element>;
  enable: Map<string, Element>;
  labels: Map<string, LocTitles>;
  hidden: string[];
}

function rulesFor(refs: Element[], defs: Map<string, Element>): RibbonRule[] {
  return refs.flatMap((ref) => {
    const id = attr(ref, 'Id');
    const def = defs.get(id);
    if (!def) {
      throw new Unavailable(
        id.startsWith('Mscrm.') ? `platform rule '${id}' is not available in this version` : `rule '${id}' is not defined`,
      );
    }
    return Array.from(def.children).map(ruleOf);
  });
}

function actionsOf(command: Element): RibbonAction[] {
  return childElements(command, 'Actions').flatMap((actions) =>
    Array.from(actions.children).map((a): RibbonAction => {
      if (a.tagName === 'Url') throw new Unavailable('Url actions are not supported in this version');
      if (a.tagName !== 'JavaScriptFunction') throw new Unavailable(`action '${a.tagName}' is not supported in this version`);
      return { library: libraryOf(a), functionName: attr(a, 'FunctionName'), parameters: parametersOf(a) };
    }),
  );
}

function commandOf(action: Element, m: Merged, lcid: number): RibbonCommand {
  const id = attr(action, 'Id');
  const rawSequence = action.getAttribute('Sequence')?.trim() ?? '';
  const sequence = rawSequence === '' ? NaN : Number(rawSequence);
  const control = childElements(action, 'CommandUIDefinition')[0]?.children[0];
  const command: RibbonCommand = {
    id,
    buttonId: control ? attr(control, 'Id') : '',
    sequence: Number.isFinite(sequence) ? sequence : Number.MAX_SAFE_INTEGER,
    label: id,
    displayRules: [],
    enableRules: [],
    actions: [],
  };
  if (!control) {
    command.unavailable = 'it has no control';
    return command;
  }
  const labelText = control.getAttribute('LabelText');
  command.label = labelText !== null ? resolve(labelText, m.labels, lcid) : command.buttonId || id;
  const tip = ['ToolTipTitle', 'ToolTipDescription']
    .map((n) => resolve(attr(control, n), m.labels, lcid))
    .filter((t) => t !== '');
  if (tip.length > 0) command.tooltip = tip.join('\n');
  const icon = control.getAttribute('ModernImage');
  if (icon) command.icon = icon;
  try {
    if (control.tagName !== 'Button') throw new Unavailable(`control '${control.tagName}' is not supported in this version`);
    const commandId = attr(control, 'Command');
    const def = m.commands.get(commandId);
    if (!def) throw new Unavailable(`command '${commandId}' is not defined`);
    command.displayRules = rulesFor(nested(def, 'DisplayRules', 'DisplayRule'), m.display);
    command.enableRules = rulesFor(nested(def, 'EnableRules', 'EnableRule'), m.enable);
    command.actions = actionsOf(def);
  } catch (e) {
    if (!(e instanceof Unavailable)) throw e;
    command.unavailable = e.message;
  }
  return command;
}

/** A diff is the inner XML of `<RibbonDiffXml>`; a string that already has the root element is read as is. */
function rootOf(diff: string): Element {
  const text = diff.replace(/^\s*<\?xml[^>]*\?>/, '').trim();
  return parseXml(/^<RibbonDiffXml[\s>/]/.test(text) ? text : `<RibbonDiffXml>${text}</RibbonDiffXml>`).documentElement;
}

/**
 * The commands a form of `entity` shows, from the diffs oldest first (UI-1e ruling E2: a later element with the same Id
 * replaces the earlier one, in its original position). Only `Mscrm.Form.{entity}.` locations are read; grid locations
 * are ignored. Ordered by `Sequence`, then document order.
 */
export function formCommands(entity: string, diffs: string[], lcid: number): { commands: RibbonCommand[]; warnings: string[] } {
  const m: Merged = {
    actions: new Map(),
    commands: new Map(),
    display: new Map(),
    enable: new Map(),
    labels: new Map(),
    hidden: [],
  };
  const warnings: string[] = [];
  const prefix = `Mscrm.Form.${entity}.`.toLowerCase();
  const isForm = (location: string): boolean => location.toLowerCase().startsWith(prefix);

  for (const diff of diffs) {
    let root: Element;
    try {
      root = rootOf(diff);
    } catch (e) {
      warnings.push(`A ribbon definition could not be read: ${e instanceof Error ? e.message : String(e)}`);
      continue;
    }
    for (const a of nested(root, 'CustomActions', 'CustomAction')) m.actions.set(attr(a, 'Id'), a);
    for (const h of nested(root, 'CustomActions', 'HideCustomAction')) {
      const location = attr(h, 'Location');
      if (isForm(location)) m.hidden.push(location);
    }
    for (const c of nested(root, 'CommandDefinitions', 'CommandDefinition')) m.commands.set(attr(c, 'Id'), c);
    for (const rules of childElements(root, 'RuleDefinitions')) {
      for (const r of nested(rules, 'DisplayRules', 'DisplayRule')) m.display.set(attr(r, 'Id'), r);
      for (const r of nested(rules, 'EnableRules', 'EnableRule')) m.enable.set(attr(r, 'Id'), r);
    }
    for (const l of nested(root, 'LocLabels', 'LocLabel')) m.labels.set(attr(l, 'Id'), locTitles(l));
  }

  for (const location of new Set(m.hidden)) warnings.push(`Hiding '${location}' is not supported in this version`);

  const commands = [...m.actions.values()]
    .filter((a) => isForm(attr(a, 'Location')))
    .map((a, index) => ({ command: commandOf(a, m, lcid), index }))
    .sort((a, b) => a.command.sequence - b.command.sequence || a.index - b.index)
    .map((c) => c.command);
  return { commands, warnings };
}
