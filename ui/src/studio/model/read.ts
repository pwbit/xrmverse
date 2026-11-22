/**
 * Text → model for the workflow studio: a TypeScript mirror of the 2.21 reader (`WorkflowYaml.Read`, src/Xrmd.Schema) for the
 * document's SHAPE only (ruling W1): kinds, keys, nesting, and which step is allowed for which kind and where. Tables,
 * columns, types and values are checked by the server (`apply?dryRun=true`), never here.
 *
 * Unlike the server, which stops at the first error, every problem found is reported. A model is returned only for a
 * document without problems, so the canvas never draws a document the code view has to fix first.
 */
import { isAlias, isCollection, isMap, isNode, isScalar, isSeq, LineCounter, parseDocument, visit, YAMLSeq } from 'yaml';
import type { Document, Node, Pair, ToStringOptions, YAMLError, YAMLMap } from 'yaml';
import { ARGUMENT_KEYS, KIND_KEYS, RULE_ACTIONS, STEP_KEYS, STEP_KINDS, TOP_KEYS, TRIGGER_KEYS } from './keys';
import type { Branch, ModelProblem, Phrase, ProcessKind, StepNode, StepPath, StepType, Words, WorkflowModel } from './types';

/** The deepest YAML nesting the server reads (`WorkflowYaml.MaxDepth`). */
const MAX_DEPTH = 64;

const KINDS: ProcessKind[] = ['workflow', 'businessRule', 'action'];

/** The keys a step needs with a value that is not empty; `value` (setOutput, setValue, setDefault) may hold `~`. */
const REQUIRED: Record<StepType, string[]> = {
  create: ['entity', 'set'],
  update: ['record', 'set'],
  assign: ['record', 'owner'],
  setState: ['record', 'state', 'status'],
  sendEmail: ['set'],
  if: ['when', 'then'],
  wait: ['then'],
  stop: ['status'],
  startChild: ['workflow'],
  performAction: ['message'],
  customActivity: ['type', 'assembly'],
  setOutput: ['argument'],
  setValue: ['field'],
  setDefault: ['field'],
  clearValue: ['field'],
  showError: ['message'],
  setVisibility: ['field', 'visible'],
  setLock: ['field', 'locked'],
  setRequirement: ['field', 'level'],
};

/** The keys that must be there but may hold the empty value. */
const PRESENT: Partial<Record<StepType, string[]>> = { setOutput: ['value'], setValue: ['value'], setDefault: ['value'] };

/** The line counters of the documents `parseText` made, for `readModel` positions. */
const counters = new WeakMap<Document.Parsed, LineCounter>();

/**
 * Parses the text (comments and source tokens kept) and reads its model. The failsafe schema keeps every scalar as the text
 * it was written with, as the server's reader does: nothing is coerced, so `0123`, `1e3`, `+1` or a long number print back
 * unchanged after an edit elsewhere, and the model holds the source text (`true`, `100`, `null` are strings).
 */
export function parseText(text: string): { doc: Document.Parsed; model?: WorkflowModel; problems: ModelProblem[] } {
  const lineCounter = new LineCounter();
  const doc = parseDocument(text, { keepSourceTokens: true, lineCounter, schema: 'failsafe' });
  useCanonicalStyle(doc);
  counters.set(doc, lineCounter);
  return { doc, ...readModel(doc, lineCounter) };
}

/** A flow sequence written without inner padding (`[a, b]`), as `YamlText.FlowText` writes it. */
function flowSeqToString(this: YAMLSeq, ...args: Parameters<YAMLSeq['toString']>): string {
  const text = YAMLSeq.prototype.toString.apply(this, args);
  return text.startsWith('[ ') && text.endsWith(' ]') && !text.includes('\n') ? `[${text.slice(2, -2)}]` : text;
}

/**
 * Makes `doc.toString()` print the format's canonical style, so that a canonical document (the 2.21 fixtures, an export)
 * prints back byte for byte: no line folding (a flow mapping stays on one line however long it is), flow mappings padded
 * (`{ field: a }`, the library's default) and flow sequences not (`[a, b]`). The library has one padding option for both,
 * so each sequence, including one added after parsing, gets its own writer. Options passed to `toString` still win.
 */
export function useCanonicalStyle(doc: Document): void {
  const toString = doc.toString.bind(doc);
  doc.toString = (options?: ToStringOptions) => {
    visit(doc, {
      Seq(_, seq) {
        if (!Object.prototype.hasOwnProperty.call(seq, 'toString')) seq.toString = flowSeqToString;
      },
    });
    return toString({ lineWidth: 0, ...options });
  };
}

/**
 * Reads the model of a parsed document. Positions come from `lineCounter`, by default the one `parseText` used for this
 * document; for another document they are counted over its current text, which is exact only while it is unchanged.
 */
export function readModel(
  doc: Document.Parsed,
  lineCounter: LineCounter | undefined = counters.get(doc),
): { model?: WorkflowModel; problems: ModelProblem[] } {
  if (doc.errors.length > 0) return { problems: doc.errors.map(syntaxProblem) };
  const reader = new Reader(doc, lineCounter ?? countLines(doc.toString()));
  const model = reader.read();
  return reader.problems.length > 0 ? { problems: reader.problems } : { model, problems: [] };
}

function syntaxProblem(error: YAMLError): ModelProblem {
  const at = error.linePos?.[0] ?? { line: 1, col: 1 };
  // The parser's message is "<message> at line L, column C:" followed by a source excerpt; keep the message.
  const message = error.message.split('\n')[0].replace(/ at line \d+, column \d+:?$/, '');
  return { message, line: at.line, column: at.col };
}

function countLines(text: string): LineCounter {
  const counter = new LineCounter();
  counter.addNewLine(0);
  for (let i = text.indexOf('\n'); i >= 0; i = text.indexOf('\n', i + 1)) counter.addNewLine(i + 1);
  return counter;
}

function article(word: string): string {
  return /^[aeiou]/.test(word) ? `an ${word}` : `a ${word}`;
}

function capitalize(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function describeKind(kind: ProcessKind): string {
  return kind === 'workflow' ? 'a workflow' : kind === 'businessRule' ? 'a business rule' : 'an action';
}

/** A plain scalar written as `text` (a quoted one is text, whatever it says). */
function isPlain(node: unknown, ...texts: string[]): boolean {
  return isScalar(node) && node.type === 'PLAIN' && texts.includes(String(node.value ?? ''));
}

/** The empty value: nothing, `~` or `null` written plain; a quoted 'null' is text. */
function isEmpty(node: unknown): boolean {
  return node == null || (isScalar(node) && node.value == null) || isPlain(node, '', '~', 'null', 'Null', 'NULL');
}

function isStepType(type: string): type is StepType {
  return Object.prototype.hasOwnProperty.call(STEP_KEYS, type);
}

/** Where a step stands, for the placement rules. */
export interface Place {
  kind: ProcessKind;
  /** A background workflow (the only place a `wait` may be). */
  background: boolean;
  /** Inside an `if` branch: where a business rule's actions go. */
  insideIf: boolean;
}

/**
 * Why the step `type` may not stand at `place`, or undefined when it may: which step is allowed for which kind and where
 * (docs/workflow-authoring.md, "What is refused and why"). The reader reports it; the step palette offers only the steps
 * without one.
 */
export function placeProblem(type: StepType, place: Place): string | undefined {
  if (place.kind === 'businessRule') {
    if (!STEP_KINDS[type].includes('businessRule'))
      return `The step '${type}' cannot be used in a business rule (a business rule takes if, ${RULE_ACTIONS.join(', ')}).`;
    if (RULE_ACTIONS.includes(type) && !place.insideIf)
      return `The step '${type}' must be inside an if: a business rule action outside a condition is not supported.`;
    return undefined;
  }
  if (RULE_ACTIONS.includes(type)) return `The step '${type}' is only valid in a business rule.`;
  if (type === 'setOutput' && place.kind !== 'action') return "The step 'setOutput' is only valid in an action.";
  if (type === 'wait' && !place.background)
    return "The step 'wait' is only valid in a background workflow (not in a real-time workflow, a business rule or an action).";
  return undefined;
}

class Reader {
  readonly problems: ModelProblem[] = [];

  constructor(
    private readonly doc: Document.Parsed,
    private readonly lines: LineCounter,
  ) {}

  read(): WorkflowModel | undefined {
    this.scan();
    const root = this.doc.contents;
    if (isEmpty(root)) {
      this.problems.push({ message: 'The workflow is empty.', line: 1, column: 1 });
      return undefined;
    }
    if (!isMap(root)) {
      this.report(root, 'The workflow must be a mapping (key: value).');
      return undefined;
    }
    const kindNode = root.get('kind', true);
    if (isEmpty(kindNode)) {
      this.report(root, "The workflow needs the key 'kind' (workflow, businessRule or action).");
      return undefined;
    }
    const kindText = this.text(kindNode, 'kind');
    if (kindText === undefined) return undefined;
    if (!(KINDS as string[]).includes(kindText)) {
      this.report(kindNode, `'kind' is '${kindText}', which is not supported (${KINDS.join(' | ')}).`);
      return undefined;
    }
    const kind = kindText as ProcessKind;

    const allowed = [...TOP_KEYS, ...KIND_KEYS[kind]];
    const fields = new Map<string, Pair>();
    for (const pair of root.items) {
      const key = this.keyText(pair, 'the workflow');
      if (key === undefined) continue;
      fields.set(key, pair);
      if (allowed.includes(key)) continue;
      const owner = KINDS.find((other) => other !== kind && KIND_KEYS[other].includes(key));
      this.report(
        pair.key,
        owner
          ? `The key '${key}' is only valid in ${describeKind(owner)}, not in ${describeKind(kind)}.`
          : `Unknown key '${key}' in the workflow (allowed: ${allowed.join(', ')}).`,
      );
    }
    const value = (key: string) => fields.get(key)?.value;

    for (const key of ['name', 'entity']) {
      if (isEmpty(value(key))) this.report(root, `The workflow needs the key '${key}'.`);
      else this.text(value(key), key);
    }

    let mode: 'realtime' | 'background' | undefined;
    if (kind === 'workflow') {
      const modeNode = value('mode');
      if (isEmpty(modeNode)) this.report(root, "The workflow needs the key 'mode' (realtime or background).");
      else {
        const text = this.text(modeNode, 'mode');
        if (text === 'realtime' || text === 'background') mode = text;
        else if (text !== undefined) this.report(modeNode, `'mode' is '${text}', which is not supported (realtime | background).`);
      }
      const only = (key: string, where: string) => {
        const pair = fields.get(key);
        if (pair) this.report(pair.key, `The key '${key}' is only valid in ${where}.`);
      };
      if (mode === 'background') {
        only('runAs', 'a real-time workflow');
        only('logFailures', 'a real-time workflow');
      } else if (mode === 'realtime') only('autoDelete', 'a background workflow');
      if (fields.has('trigger')) this.trigger(value('trigger'), fields.get('trigger')!, mode !== 'background');
    }
    if (kind === 'action') {
      if (isEmpty(value('message'))) this.report(root, "The workflow needs the key 'message'.");
      else this.text(value('message'), 'message');
      if (fields.has('arguments')) this.arguments(value('arguments'), fields.get('arguments')!);
    }

    let steps: StepNode[] = [];
    if (!fields.has('steps')) this.report(root, "The workflow needs the key 'steps'.");
    else steps = this.steps(value('steps'), fields.get('steps')!, ['steps'], { kind, background: mode === 'background', insideIf: false });

    const header: Record<string, unknown> = {};
    for (const [key, pair] of fields) if (key !== 'trigger' && key !== 'steps') header[key] = this.js(pair.value);
    const scalar = (key: string) => (isScalar(value(key)) && !isEmpty(value(key)) ? String((value(key) as { value: unknown }).value) : undefined);
    return {
      kind,
      id: scalar('id'),
      name: scalar('name') ?? '',
      entity: scalar('entity'),
      mode,
      header,
      trigger: fields.has('trigger') ? this.js(value('trigger')) : undefined,
      steps,
    };
  }

  // ------------------------------------------------------------------ positions and helpers

  private report(node: unknown, message: string): void {
    const offset = isNode(node) && node.range ? node.range[0] : 0;
    const { line, col } = this.lines.linePos(offset);
    this.problems.push({ message, line, column: col });
  }

  private line(node: unknown): number {
    return isNode(node) && node.range ? this.lines.linePos(node.range[0]).line : 1;
  }

  private js(node: unknown): unknown {
    return isNode(node) ? node.toJS(this.doc) : node;
  }

  /** Anchors, aliases and the nesting limit, refused as the server's scan does. */
  private scan(): void {
    visit(this.doc, (_, node, path) => {
      if (isAlias(node) || (isNode(node) && node.anchor)) {
        this.report(node, 'Anchors and aliases are not supported in a workflow.');
        return;
      }
      if (isCollection(node) && path.filter((ancestor) => isCollection(ancestor)).length + 1 > MAX_DEPTH) {
        this.report(node, `The workflow is nested deeper than ${MAX_DEPTH} levels.`);
        return visit.SKIP;
      }
      return undefined;
    });
  }

  /** The text of a single value; a list or a mapping is reported. */
  private text(node: unknown, key: string): string | undefined {
    if (!isScalar(node)) {
      this.report(node, `'${key}' must be a single value.`);
      return undefined;
    }
    return String(node.value);
  }

  private keyText(pair: Pair, what: string): string | undefined {
    if (isScalar(pair.key) && pair.key.value != null) return String(pair.key.value);
    this.report(pair.key, `A key of ${what} must be a single word.`);
    return undefined;
  }

  /** The keys of a mapping, each unknown key reported with the allowed list. */
  private fields(map: YAMLMap, what: string, allowed: string[]): Map<string, Pair> {
    const fields = new Map<string, Pair>();
    for (const pair of map.items) {
      const key = this.keyText(pair, what);
      if (key === undefined) continue;
      if (!allowed.includes(key)) this.report(pair.key, `Unknown key '${key}' in ${what} (allowed: ${allowed.join(', ')}).`);
      else fields.set(key, pair);
    }
    return fields;
  }

  /** A mapping or a report against `fallback` (the key) when the value is missing. */
  private mapping(node: unknown, fallback: unknown, what: string): YAMLMap | undefined {
    if (isMap(node)) return node;
    this.report(isNode(node) ? node : fallback, `${capitalize(what)} must be a mapping (key: value).`);
    return undefined;
  }

  // ------------------------------------------------------------------ header

  private trigger(node: unknown, pair: Pair, realtime: boolean): void {
    const map = this.mapping(node, pair.key, 'the trigger');
    if (!map) return;
    const fields = this.fields(map, 'the trigger', TRIGGER_KEYS);
    if (map.items.length === 0) this.report(map, 'The trigger must name create, update or delete.');
    const stage = (body: YAMLMap, what: string) => {
      const stagePair = this.fields(body, what, what === 'the update trigger' ? ['columns', 'stage'] : ['stage']).get('stage');
      if (stagePair && !realtime) this.report(stagePair.key, 'A stage is only valid in a real-time workflow.');
    };
    for (const event of ['create', 'delete']) {
      const value = fields.get(event)?.value;
      if (!fields.has(event)) continue;
      if (isMap(value)) stage(value, `the ${event} trigger`);
      else if (!isPlain(value, 'true'))
        this.report(value ?? fields.get(event)!.key, `'${event}' must be true or { stage: ${event === 'create' ? 'pre | post' : 'pre'} }.`);
    }
    if (fields.has('update')) {
      const update = fields.get('update')!;
      if (!isMap(update.value)) {
        this.report(update.value ?? update.key, "'update' must be { columns: [<column>, ...], stage?: pre | post }: an update trigger needs the columns it watches.");
        return;
      }
      stage(update.value, 'the update trigger');
      const columns = update.value.get('columns', true);
      if (!isSeq(columns) || columns.items.length === 0)
        this.report(columns ?? update.value, "The update trigger needs 'columns', a non-empty list of the columns it watches.");
    }
  }

  private arguments(node: unknown, pair: Pair): void {
    if (!isSeq(node)) {
      this.report(node ?? pair.key, "'arguments' must be a list.");
      return;
    }
    for (const item of node.items) {
      const map = this.mapping(item, node, 'an argument');
      if (!map) continue;
      const fields = this.fields(map, 'an argument', ARGUMENT_KEYS);
      for (const key of ['name', 'direction', 'type'])
        if (isEmpty(fields.get(key)?.value)) this.report(map, `An argument needs the key '${key}'.`);
    }
  }

  // ------------------------------------------------------------------ steps

  private steps(node: unknown, owner: Pair, path: StepPath, place: Place): StepNode[] {
    if (!isSeq(node)) {
      this.report(node ?? owner.key, 'Steps must be a list (- kind: ...).');
      return [];
    }
    const steps: StepNode[] = [];
    node.items.forEach((item, index) => {
      const step = this.step(item, node, [...path, index], place);
      if (step) steps.push(step);
    });
    return steps;
  }

  private step(item: unknown, list: Node, path: StepPath, place: Place): StepNode | undefined {
    if (!isMap(item) || item.items.length === 0) {
      this.report(isNode(item) ? item : list, 'A step must be a mapping with one key, the kind of step (for example create: { ... }).');
      return undefined;
    }
    if (item.items.length > 1) {
      const [first, second] = item.items;
      this.report(second.key, `A step takes exactly one kind; '${String(first.key)}' and '${String(second.key)}' are both given.`);
      return undefined;
    }
    const pair = item.items[0];
    const type = this.keyText(pair, 'a step');
    if (type === undefined) return undefined;
    if (!isStepType(type)) {
      this.report(
        pair.key,
        `Unknown step '${type}' (allowed: ${Object.keys(STEP_KEYS).join(', ')}). A step the format lacks (a dialog, a recommendation, a template) has no equivalent: use a webhook or a custom workflow activity.`,
      );
      return undefined;
    }
    this.checkPlace(pair.key, type, place);

    const what = `${article(type)} step`;
    const body = this.mapping(pair.value, pair.key, what);
    const fields = body ? this.fields(body, what, STEP_KEYS[type]) : new Map<string, Pair>();
    if (body) {
      for (const key of REQUIRED[type]) {
        if (isEmpty(fields.get(key)?.value)) {
          // elseIf and else belong to the when branch: an if without it is refused as such.
          const message =
            type === 'if' && key === 'when' && (fields.has('elseIf') || fields.has('else'))
              ? "An if step needs the key 'when': 'elseIf' and 'else' need a when branch."
              : `${capitalize(what)} needs the key '${key}'.`;
          this.report(body, message);
        }
      }
      for (const key of PRESENT[type] ?? []) if (!fields.has(key)) this.report(body, `${capitalize(what)} needs the key '${key}'.`);
      if (type === 'wait') {
        if (!fields.has('until') && !fields.has('timeout')) this.report(body, "A wait step needs 'until' or 'timeout' (or both).");
        if (fields.has('onTimeout') && !fields.has('timeout')) this.report(fields.get('onTimeout')!.key, "'onTimeout' needs a 'timeout'.");
      }
    }

    const value = (key: string) => fields.get(key)?.value;
    const stepPath = [...path, type];
    const branches: Branch[] = [];
    const branch = (label: Branch['label'], key: string, branchPath: StepPath, inner: Place, when?: unknown) => {
      const owner = fields.get(key);
      if (!owner || isEmpty(owner.value)) return;
      const steps = this.steps(owner.value, owner, branchPath, inner);
      branches.push(when === undefined ? { label, path: branchPath, steps } : { label, path: branchPath, steps, when });
    };
    if (type === 'if') {
      const inner = { ...place, insideIf: true };
      branch('then', 'then', [...stepPath, 'then'], inner, this.js(value('when')));
      if (fields.has('elseIf')) this.elseIf(fields.get('elseIf')!, [...stepPath, 'elseIf'], inner, branches);
      branch('else', 'else', [...stepPath, 'else'], inner);
    } else if (type === 'wait') {
      branch('then', 'then', [...stepPath, 'then'], place, fields.has('until') ? this.js(value('until')) : undefined);
      branch('onTimeout', 'onTimeout', [...stepPath, 'onTimeout'], place);
    }

    const js = (body ? this.js(body) : {}) as Record<string, unknown>;
    const description = value('description');
    const id = value('id');
    const entity = type === 'sendEmail' ? 'email' : type === 'create' && isScalar(value('entity')) && !isEmpty(value('entity')) ? String((value('entity') as { value: unknown }).value) : undefined;
    return {
      type,
      path,
      ...(isScalar(description) && !isEmpty(description) ? { description: String(description.value) } : {}),
      ...(isScalar(id) && !isEmpty(id) ? { id: String(id.value) } : {}),
      ...(entity !== undefined ? { entity } : {}),
      summary: summarize(type, js),
      branches,
      line: this.line(item),
    };
  }

  private elseIf(pair: Pair, path: StepPath, place: Place, branches: Branch[]): void {
    if (!isSeq(pair.value)) {
      this.report(pair.value ?? pair.key, "'elseIf' must be a list of { when, then }.");
      return;
    }
    pair.value.items.forEach((item, index) => {
      const map = this.mapping(item, pair.value, 'an elseIf branch');
      if (!map) return;
      const fields = this.fields(map, 'an elseIf branch', ['when', 'then']);
      for (const key of ['when', 'then']) if (isEmpty(fields.get(key)?.value)) this.report(map, `An elseIf branch needs the key '${key}'.`);
      const then = fields.get('then');
      if (!then || isEmpty(then.value)) return;
      const branchPath = [...path, index, 'then'];
      branches.push({ label: 'elseIf', path: branchPath, steps: this.steps(then.value, then, branchPath, place), when: this.js(fields.get('when')?.value) });
    });
  }

  private checkPlace(key: unknown, type: StepType, place: Place): void {
    const problem = placeProblem(type, place);
    if (problem) this.report(key, problem);
  }
}

// -------------------------------------------------------------------- summaries

const phrase = (key: Phrase['key'], args?: Phrase['args']): Phrase => (args ? { key, args } : { key });

function word(value: unknown): Words {
  if (value == null) return '?';
  if (typeof value === 'object') return valueText(value);
  return String(value);
}

function recordText(record: unknown): Words {
  if (record && typeof record === 'object') {
    const map = record as Record<string, unknown>;
    if ('related' in map) return phrase('studio.summary.related', { lookup: word(map.related) });
    if ('created' in map) return phrase('studio.summary.created', { id: word(map.created) });
  }
  return word(record);
}

/** A value in a few words: a literal as written, a dynamic value by its main operand. */
function valueText(value: unknown): Words {
  if (value === null) return '~';
  if (typeof value !== 'object') return String(value);
  if (Array.isArray(value)) return `[${value.length}]`;
  const map = value as Record<string, unknown>;
  for (const key of ['field', 'arg', 'output']) if (key in map) return word(map[key]);
  if ('record' in map) return recordText(map.record);
  if ('now' in map) return phrase('studio.summary.now');
  if ('lookup' in map) return word(map.name ?? map.lookup);
  const [first] = Object.keys(map);
  return first ?? '?';
}

/**
 * A condition in a few words, as the card summaries write it ("revenue gt 1000", "all of 2 conditions"), in words the UI
 * translates (`phraseText`); columns, operators and values stay as written.
 */
export function conditionText(condition: unknown): Words {
  if (!condition || typeof condition !== 'object' || Array.isArray(condition)) return word(condition);
  const map = condition as Record<string, unknown>;
  const count = (items: unknown) => (Array.isArray(items) ? items.length : 0);
  if ('all' in map) return phrase('studio.summary.all', { count: count(map.all) });
  if ('any' in map) return phrase('studio.summary.any', { count: count(map.any) });
  if ('not' in map) return phrase('studio.summary.not', { condition: conditionText(map.not) });
  const op = word(map.op);
  if (op === 'null' || op === 'notNull' || !('value' in map)) return phrase('studio.summary.check', { left: valueText(map), op });
  return phrase('studio.summary.compare', { left: valueText(map), op, right: valueText(map.value) });
}

function summarize(type: StepType, body: Record<string, unknown>): Phrase {
  switch (type) {
    case 'create':
      return phrase('studio.summary.create', { table: word(body.entity) });
    case 'update':
      return phrase('studio.summary.update', { record: recordText(body.record) });
    case 'assign':
      return phrase('studio.summary.assign', { record: recordText(body.record) });
    case 'setState':
      return phrase('studio.summary.setState', { record: recordText(body.record) });
    case 'sendEmail':
      return phrase('studio.summary.sendEmail');
    case 'if':
      return phrase('studio.summary.if', { condition: conditionText(body.when) });
    case 'wait': {
      const timeout =
        body.timeout !== undefined && (body.timeout === null || typeof body.timeout !== 'object') ? word(body.timeout) : phrase('studio.summary.aTimeout');
      if (body.until !== undefined && body.timeout !== undefined) return phrase('studio.summary.waitUntilOrFor', { condition: conditionText(body.until), timeout });
      return body.until !== undefined ? phrase('studio.summary.waitUntil', { condition: conditionText(body.until) }) : phrase('studio.summary.waitFor', { timeout });
    }
    case 'stop':
      return phrase('studio.summary.stop', { status: word(body.status) });
    case 'startChild':
      return phrase('studio.summary.startChild', { workflow: word(body.workflow) });
    case 'performAction':
      return phrase('studio.summary.performAction', { message: word(body.message) });
    case 'customActivity':
      return phrase('studio.summary.customActivity', { type: word(body.type) });
    case 'setOutput':
      return phrase('studio.summary.setOutput', { argument: word(body.argument) });
    case 'setValue':
      return phrase('studio.summary.setValue', { field: word(body.field) });
    case 'setDefault':
      return phrase('studio.summary.setDefault', { field: word(body.field) });
    case 'clearValue':
      return phrase('studio.summary.clearValue', { field: word(body.field) });
    case 'showError':
      return phrase('studio.summary.showError', { message: word(body.message) });
    case 'setVisibility':
      return phrase(body.visible === 'false' ? 'studio.summary.hide' : 'studio.summary.show', { field: word(body.field) });
    case 'setLock':
      return phrase(body.locked === 'false' ? 'studio.summary.unlock' : 'studio.summary.lock', { field: word(body.field) });
    case 'setRequirement':
      return phrase('studio.summary.setRequirement', { field: word(body.field), level: word(body.level) });
  }
}
