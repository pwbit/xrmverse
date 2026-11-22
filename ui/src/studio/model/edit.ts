/**
 * The canvas operations on the workflow YAML document (ruling W3): each mutates the `yaml` Document in place, so the flow
 * style and the comments of every node it does not touch print back as they were. A step is addressed by its YAML path and
 * a branch by the path of its list (ruling W2); see `StepPath`.
 *
 * The document is parsed with the failsafe schema (`parseText`), where every scalar is text. A new scalar is therefore
 * written the way the 2.21 server's writer writes it (`YamlText.ScalarText`): plain when that reads back as the same text,
 * quoted otherwise. Without that, `''`, `'null'` or `'~'` would print plain and read back as the empty value.
 */
import { isCollection, isMap, isNode, isScalar, isSeq, Scalar, visit, YAMLSeq } from 'yaml';
import type { Document, Node, Pair, YAMLMap } from 'yaml';
import { ARGUMENT_KEYS, ELSE_IF_ORDER, HEADER_ORDER, STEP_KEYS, STEP_ORDER, TRIGGER_KEYS, UPDATE_TRIGGER_ORDER } from './keys';
import { insertProblem, moveProblem, processOfDocument, shapeOfNode } from './place';
import { FLOW_BODY, stepTemplate } from './templates';
import type { StepPath, StepType } from './types';

/** An operation the document cannot take (a path that is not a step or a branch, a move into the step itself). */
export class StudioEditError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'StudioEditError';
  }
}

// -------------------------------------------------------------------- scalars

const NULL_TEXTS = ['', '~', 'null', 'Null', 'NULL'];

/** `char.IsControl` of .NET: the Unicode category Cc. */
function isControl(code: number): boolean {
  return code <= 0x1f || (code >= 0x7f && code <= 0x9f);
}

function isSurrogate(code: number): boolean {
  return code >= 0xd800 && code <= 0xdfff;
}

function isSpecial(code: number): boolean {
  return isControl(code) || code === 0x2028 || code === 0x2029 || code === 0xfeff;
}

/**
 * Whether `value` can be written without quotes, in a block or a flow context, and read back as the same text: the rule of
 * the 2.21 server's writer (`YamlText.IsPlainSafe`, src/Xrmd.Schema/Workflows/YamlText.cs), mirrored code unit by code unit.
 * `true`, `1` or `0123` are plain: the format reads every scalar as text.
 */
export function isPlainSafe(value: string): boolean {
  if (value.length === 0 || NULL_TEXTS.includes(value) || value.includes('\\')) return false;
  if (value.startsWith(' ') || value.endsWith(' ') || value.startsWith('---') || value.startsWith('...')) return false;
  if ('#,[]{}&*!|>\'"%@`:'.includes(value[0])) return false;
  if ((value[0] === '-' || value[0] === '?') && (value.length === 1 || ' ,[]{}'.includes(value[1]))) return false;
  for (let i = 0; i < value.length; i++) {
    const c = value[i];
    const code = value.charCodeAt(i);
    if (isSpecial(code) || ',[]{}'.includes(c) || isSurrogate(code)) return false;
    if (c === ':' && (i + 1 === value.length || ' ,[]{}'.includes(value[i + 1]))) return false;
    if (c === '#' && i > 0 && value[i - 1] === ' ') return false;
  }
  return true;
}

/** Whether `value` needs double quotes (an escape): a control character, a line break, a lone surrogate. */
function needsEscapes(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (isSpecial(code)) return true;
    if (code >= 0xd800 && code <= 0xdbff && i + 1 < value.length && value.charCodeAt(i + 1) >= 0xdc00 && value.charCodeAt(i + 1) <= 0xdfff) i++;
    else if (isSurrogate(code)) return true;
  }
  return false;
}

/** Sets the scalar's text and style: `null` is the empty value `~`; a number or a Boolean is its text, plain. */
function styleScalar(scalar: Scalar): void {
  const value = scalar.value;
  if (value == null) {
    scalar.value = '~';
    scalar.type = Scalar.PLAIN;
    return;
  }
  const text = typeof value === 'string' ? value : String(value);
  scalar.value = text;
  scalar.type = isPlainSafe(text) ? Scalar.PLAIN : needsEscapes(text) ? Scalar.QUOTE_DOUBLE : Scalar.QUOTE_SINGLE;
}

/**
 * A text written plain whatever it says. The operator `null` of a comparison is written `op: null`, as the server's writer
 * does: the format reads `op` as text (`nullIsText`), whereas a plain `null` anywhere else is the empty value, so `setAt`
 * would quote the text 'null'.
 */
export class PlainText {
  constructor(readonly text: string) {}
}

/** `value` with each `PlainText` replaced by a plain scalar node, collected in `kept` so that `styleScalar` leaves it. */
function withPlainTexts(value: unknown, kept: Set<Scalar>): unknown {
  if (value instanceof PlainText) {
    const scalar = new Scalar(value.text);
    scalar.type = Scalar.PLAIN;
    kept.add(scalar);
    return scalar;
  }
  if (Array.isArray(value)) return value.map((item) => withPlainTexts(item, kept));
  if (value && typeof value === 'object' && !isNode(value)) {
    const out: Record<string, unknown> = {};
    for (const [key, item] of Object.entries(value)) out[key] = withPlainTexts(item, kept);
    return out;
  }
  return value;
}

/**
 * A new node for a plain JS value: scalars styled as the server writes them, collections in flow style (a value or a
 * condition is one line in the canonical form).
 */
function valueNode(doc: Document, value: unknown, flow = true): Node {
  const kept = new Set<Scalar>();
  const node = doc.createNode(withPlainTexts(value, kept)) as Node;
  if (isScalar(node) && kept.has(node)) return node;
  visit(node, {
    Collection(_, collection) {
      collection.flow = flow;
    },
    Scalar(_, scalar) {
      if (!kept.has(scalar)) styleScalar(scalar);
    },
  });
  return node;
}

// -------------------------------------------------------------------- paths

function describe(path: (string | number)[]): string {
  return path.map(String).join('.');
}

/** Whether `path` starts with `prefix` (or equals it). */
function startsWith(path: (string | number)[], prefix: (string | number)[]): boolean {
  return prefix.length <= path.length && prefix.every((key, i) => path[i] === key);
}

/** The empty value as the format reads it: a missing node, `~`, `null` or nothing written plain. */
function isEmptyNode(node: unknown): boolean {
  return node == null || (isScalar(node) && (node.value == null || (node.type === Scalar.PLAIN && NULL_TEXTS.includes(String(node.value)))));
}

function nodeAt(doc: Document, path: (string | number)[]): unknown {
  return path.length === 0 ? doc.contents : doc.getIn(path, true);
}

/** The step list at `branch`; a missing or empty one is created when `create` is set (and its owner is a mapping). */
function listAt(doc: Document, branch: StepPath, create: boolean): YAMLSeq | undefined {
  const node = nodeAt(doc, branch);
  if (isSeq(node)) return node;
  const owner = nodeAt(doc, branch.slice(0, -1));
  if (branch.length > 0 && isEmptyNode(node) && isMap(owner) && typeof branch[branch.length - 1] === 'string') {
    if (!create) return undefined;
    const list = new YAMLSeq();
    list.flow = true;
    doc.setIn(branch, list);
    return list;
  }
  throw new StudioEditError(`'${describe(branch)}' is not a list of steps.`);
}

/** The list holding the step at `path` and its index there. */
function stepAt(doc: Document, path: StepPath): { list: YAMLSeq; index: number } {
  const index = path[path.length - 1];
  const list = nodeAt(doc, path.slice(0, -1));
  if (typeof index !== 'number' || !isSeq(list) || index < 0 || index >= list.items.length || !isMap(list.items[index]))
    throw new StudioEditError(`'${describe(path)}' is not a step.`);
  return { list, index };
}

/** The body mapping of the step at `path`, which must be of `type`. */
function bodyAt(doc: Document, path: StepPath, type: StepType): YAMLMap {
  const body = doc.getIn([...path, type], true);
  if (!isMap(body)) throw new StudioEditError(`'${describe(path)}' is not ${type === 'if' ? 'an if' : `a ${type}`} step.`);
  return body;
}

function insertAt(list: YAMLSeq, index: number, node: unknown): void {
  // An empty list is written `[]`; with a step in it, it becomes a block list again.
  if (list.items.length === 0) list.flow = false;
  list.items.splice(index, 0, node);
}

function removeAt(list: YAMLSeq, index: number): unknown {
  const [node] = list.items.splice(index, 1);
  if (list.items.length === 0) list.flow = true;
  return node;
}

/** The new node of a step: the body a flow mapping for the one-line kinds, its values in flow style otherwise. */
function stepNode(doc: Document, type: StepType): Node {
  const node = doc.createNode(stepTemplate(type)) as YAMLMap;
  visit(node, {
    Scalar(_, scalar) {
      styleScalar(scalar);
    },
  });
  const body = node.get(type, true);
  if (!isMap(body)) throw new StudioEditError(`The template of '${type}' is not a mapping.`);
  if (FLOW_BODY.has(type)) body.flow = true;
  else for (const pair of body.items) if (isCollection(pair.value)) pair.value.flow = true;
  return node;
}

// -------------------------------------------------------------------- operations

/** Refuses an operation that would put a step where the reader refuses it (`place.ts`). */
function checkPlace(problem: string | undefined): void {
  if (problem) throw new StudioEditError(problem);
}

/**
 * Inserts a new step of `type` (`stepTemplate`) at `index` of the branch list and returns its path. A step the place does
 * not take (a rule action outside an if, a wait outside a background workflow, ...) is refused, with the document unchanged.
 */
export function insertStep(doc: Document, branch: StepPath, index: number, type: StepType): StepPath {
  const process = processOfDocument(doc);
  if (process) checkPlace(insertProblem(type, process, branch));
  const existing = listAt(doc, branch, false);
  const length = existing?.items.length ?? 0;
  if (!Number.isInteger(index) || index < 0 || index > length)
    throw new StudioEditError(`There is no position ${index} in '${describe(branch)}' (0 to ${length}).`);
  insertAt(existing ?? listAt(doc, branch, true)!, index, stepNode(doc, type));
  return [...branch, index];
}

/** Removes the step at `path` with everything inside it. */
export function removeStep(doc: Document, path: StepPath): void {
  const { list, index } = stepAt(doc, path);
  removeAt(list, index);
}

/**
 * Moves the step at `from` to position `index` of the list `toBranch` and returns its new path. `index` is the position as
 * the list is before the move (a slot of the canvas), so moving a step down its own list by one is `index` = its index + 2.
 * A step cannot move into one of its own branches, nor to a place that does not take it or a step inside it (the same rule
 * as insert, `place.ts`); a refused move leaves the document unchanged.
 */
export function moveStep(doc: Document, from: StepPath, toBranch: StepPath, index: number): StepPath {
  const source = stepAt(doc, from);
  if (startsWith(toBranch, from)) throw new StudioEditError(`The step '${describe(from)}' cannot move into one of its own branches.`);
  const process = processOfDocument(doc);
  const shape = shapeOfNode(source.list.items[source.index]);
  if (process && shape) checkPlace(moveProblem(shape, process, toBranch));
  const target = listAt(doc, toBranch, false);
  const length = target?.items.length ?? 0;
  if (!Number.isInteger(index) || index < 0 || index > length)
    throw new StudioEditError(`There is no position ${index} in '${describe(toBranch)}' (0 to ${length}).`);

  // Removing the step shifts the later steps of its list, and the branches inside them, up by one.
  const owner = from.slice(0, -1);
  const branch = [...toBranch];
  let position = index;
  if (target === source.list) {
    if (index > source.index) position--;
  } else if (startsWith(toBranch, owner) && toBranch.length > owner.length && (toBranch[owner.length] as number) > source.index) {
    branch[owner.length] = (toBranch[owner.length] as number) - 1;
  }
  const node = removeAt(source.list, source.index);
  insertAt(target ?? listAt(doc, branch, true)!, position, node);
  return [...branch, position];
}

const isStepType = (key: unknown): key is StepType => typeof key === 'string' && Object.prototype.hasOwnProperty.call(STEP_KEYS, key);

/** The writer's key order of the mapping at `owner` (the header, the trigger, an argument, a step body), if it has one. */
export function keyOrder(owner: (string | number)[]): string[] | undefined {
  const last = owner[owner.length - 1];
  const before = owner[owner.length - 2];
  if (owner.length === 0) return HEADER_ORDER;
  if (owner.length === 1 && last === 'trigger') return TRIGGER_KEYS;
  if (owner.length === 2 && owner[0] === 'trigger' && last === 'update') return UPDATE_TRIGGER_ORDER;
  if (owner.length === 2 && owner[0] === 'arguments' && typeof last === 'number') return ARGUMENT_KEYS;
  if (typeof last === 'number' && before === 'elseIf') return ELSE_IF_ORDER;
  const list = owner[owner.length - 3];
  if (isStepType(last) && typeof before === 'number' && ['steps', 'then', 'else', 'onTimeout'].includes(String(list))) return STEP_ORDER[last];
  return undefined;
}

/** Adds `pair` to `map` at its place in `order` (before the first key that comes after it), or at the end. */
function placePair(map: YAMLMap, pair: Pair, order: string[] | undefined): void {
  const rank = order ? order.indexOf(String((pair.key as Scalar).value)) : -1;
  const at = rank < 0 ? -1 : map.items.findIndex((item) => order!.indexOf(keyText(item.key) ?? '') > rank);
  if (at < 0) map.items.push(pair);
  else map.items.splice(at, 0, pair);
}

/**
 * Sets the value at `path` (a step key, a header key, a column of `set`, ...); `undefined` deletes the key. A scalar that
 * replaces a node keeps its comments; a collection that replaces one keeps its flow or block style. A new key is written
 * with the quoting rule (`isPlainSafe`) and goes to its place in the writer's order (`keyOrder`; at the end of a mapping
 * that has none, such as `set`). The index right after the end of a list appends to it. Missing mappings on the way are
 * created (in flow style); a list index beyond that is refused.
 */
export function setAt(doc: Document, path: (string | number)[], value: unknown): void {
  if (path.length === 0) throw new StudioEditError('setAt needs a path.');
  for (let i = 0; i < path.length; i++) {
    const owner = nodeAt(doc, path.slice(0, i));
    const key = path[i];
    if (isSeq(owner) && typeof key === 'number' && key >= 0 && key < owner.items.length) continue;
    if (isSeq(owner) && i === path.length - 1 && key === owner.items.length) continue;
    if (isMap(owner) && typeof key === 'string') continue;
    const rest = path.slice(i);
    if (i > 0 && isEmptyNode(owner) && rest.every((part) => typeof part === 'string')) {
      // A missing (or empty) mapping on the way: written as one new value at the last key that is there.
      if (value === undefined) return;
      const nested = (rest as string[]).reduceRight<unknown>((inner, part) => ({ [part]: inner }), value);
      setAt(doc, path.slice(0, i), nested);
      return;
    }
    throw new StudioEditError(`'${describe(path)}' does not exist in the document.`);
  }

  if (value === undefined) {
    doc.deleteIn(path);
    return;
  }
  const ownerPath = path.slice(0, -1);
  const owner = nodeAt(doc, ownerPath);
  const key = path[path.length - 1];
  const node = valueNode(doc, value);
  if (isSeq(owner) && key === owner.items.length) {
    owner.items.push(node);
    return;
  }
  const existing = nodeAt(doc, path);
  if (existing === undefined && isMap(owner)) {
    const pair = doc.createPair(key, node) as Pair;
    styleScalar(pair.key as Scalar);
    placePair(owner, pair, keyOrder(ownerPath));
    return;
  }
  if (isScalar(existing) && isScalar(node)) {
    existing.value = node.value;
    existing.type = node.type;
    return;
  }
  if (isCollection(existing) && isCollection(node)) node.flow = existing.flow;
  if (isNode(existing)) {
    node.comment = existing.comment;
    node.commentBefore = existing.commentBefore;
  }
  doc.setIn(path, node);
}

/**
 * Gives the mapping or list at `path` the style the writer gives a `set`, `inputs` or `outputs` mapping and the `arguments`
 * list: block while it has entries (unless it sits inside a flow collection, where block is not possible), `{}` / `[]` when
 * empty. Nothing happens when there is no collection at `path`.
 */
export function blockStyle(doc: Document, path: (string | number)[]): void {
  const node = nodeAt(doc, path);
  if (!isCollection(node)) return;
  if (node.items.length === 0) {
    node.flow = true;
    return;
  }
  for (let i = 0; i < path.length; i++) {
    const ancestor = nodeAt(doc, path.slice(0, i));
    if (isCollection(ancestor) && ancestor.flow) return;
  }
  node.flow = false;
}

/** The text of a mapping key: a scalar node, or a plain JS key (`doc.setIn` adds keys that are not nodes). */
function keyText(key: unknown): string | undefined {
  if (isScalar(key)) return key.value == null ? undefined : String(key.value);
  return typeof key === 'string' || typeof key === 'number' ? String(key) : undefined;
}

/** Adds `key: value` to `map` right after the last of `after` that is there (at the end if none is). */
function addPair(doc: Document, map: YAMLMap, key: string, value: Node, after: string[]): void {
  const pair = doc.createPair(key, value) as Pair;
  styleScalar(pair.key as Scalar);
  let at = -1;
  map.items.forEach((item, i) => {
    const text = keyText(item.key);
    if (text !== undefined && after.includes(text)) at = i;
  });
  if (at < 0) map.items.push(pair);
  else map.items.splice(at + 1, 0, pair);
}

/** The branch `key` of a step body: the existing list, an empty value replaced by `[]`, or a new `[]` after `after`. */
function addBranch(doc: Document, body: YAMLMap, key: string, after: string[]): void {
  const pair = body.items.find((item) => keyText(item.key) === key);
  if (pair && isSeq(pair.value)) return;
  if (pair && !isEmptyNode(pair.value)) throw new StudioEditError(`'${key}' is not a list of steps.`);
  const list = new YAMLSeq();
  list.flow = true;
  if (pair) pair.value = list;
  else addPair(doc, body, key, list, after);
}

/** Adds an `elseIf` branch `{ when: { all: [] }, then: [] }` (before `else`) to the if at `ifPath`; returns its `then`. */
export function addElseIf(doc: Document, ifPath: StepPath): StepPath {
  const body = bodyAt(doc, ifPath, 'if');
  addBranch(doc, body, 'elseIf', ['when', 'then']);
  const list = body.get('elseIf', true);
  if (!isSeq(list)) throw new StudioEditError("'elseIf' is not a list.");
  const branch = valueNode(doc, { when: { all: [] }, then: [] }) as YAMLMap;
  branch.flow = false;
  insertAt(list, list.items.length, branch);
  return [...ifPath, 'if', 'elseIf', list.items.length - 1, 'then'];
}

/** Adds an empty `else` branch to the if at `ifPath` (an existing one is kept); returns its path. */
export function addElse(doc: Document, ifPath: StepPath): StepPath {
  addBranch(doc, bodyAt(doc, ifPath, 'if'), 'else', ['when', 'then', 'elseIf']);
  return [...ifPath, 'if', 'else'];
}

/** Adds an empty `onTimeout` branch (after `then`) to the wait at `waitPath`, which needs a timeout; returns its path. */
export function addOnTimeout(doc: Document, waitPath: StepPath): StepPath {
  const body = bodyAt(doc, waitPath, 'wait');
  if (isEmptyNode(body.get('timeout', true))) throw new StudioEditError("'onTimeout' needs a 'timeout': set the wait's timeout first.");
  addBranch(doc, body, 'onTimeout', ['until', 'timeout', 'then']);
  return [...waitPath, 'wait', 'onTimeout'];
}
