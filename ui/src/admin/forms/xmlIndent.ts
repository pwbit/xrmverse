/**
 * Whitespace for the elements the designer writes (C3). In a FormXML whose children sit on their own indented lines, a
 * new element is indented like its siblings, one step deeper per level; a compact FormXML stays compact. Only elements
 * the designer creates or re-flows are touched: an element it does not edit keeps its text.
 */

const TEXT_NODE = 3;
const DOCUMENT_NODE = 9;

const isBlank = (n: Node | null | undefined): n is Text => n?.nodeType === TEXT_NODE && !n.nodeValue?.trim();

/** The text after the last `\n` of `ws`; undefined when it has none. */
const lastLine = (ws: string): string | undefined => {
  const at = ws.lastIndexOf('\n');
  return at < 0 ? undefined : ws.slice(at + 1);
};

/** The indentation of `el`'s own line: the text after the last `\n` of the whitespace before it, else undefined. */
export function ownIndent(el: Element): string | undefined {
  const before = el.previousSibling;
  return isBlank(before) ? lastLine(before.nodeValue ?? '') : undefined;
}

/**
 * The indentation of `parent`'s children: the text after the last `\n` of the whitespace before its first element child.
 * Undefined when that whitespace has no `\n` (compact), or `parent` has no element child.
 */
export function childIndent(parent: Element): string | undefined {
  const before = parent.firstElementChild?.previousSibling;
  return isBlank(before) ? lastLine(before.nodeValue ?? '') : undefined;
}

/**
 * One indentation step: the difference between the child and own indentation of the nearest ancestor (or `el` itself)
 * that has indented children, else two spaces.
 */
export function indentStep(el: Element): string {
  for (let e: Element | null = el; e; e = e.parentElement) {
    // The document element starts its line.
    const own = e.parentNode?.nodeType === DOCUMENT_NODE ? '' : ownIndent(e);
    const child = childIndent(e);
    if (own !== undefined && child !== undefined && child.length > own.length && child.startsWith(own)) {
      return child.slice(own.length);
    }
  }
  return '  ';
}

/**
 * Indents an element the designer created: each element child on its own line at `indent + step`, recursively, and the
 * closing tag at `indent`. An element whose content is text (or that has no element child) is left as it is.
 */
export function indentTree(el: Element, indent: string, step: string): void {
  if (el.children.length === 0) return;
  const nodes = Array.from(el.childNodes);
  if (nodes.some((n) => n.nodeType === TEXT_NODE && n.nodeValue?.trim())) return;
  for (const n of nodes) if (isBlank(n)) el.removeChild(n);
  const doc = el.ownerDocument;
  for (const child of Array.from(el.children)) {
    el.insertBefore(doc.createTextNode(`\n${indent}${step}`), child);
    indentTree(child, indent + step, step);
  }
  el.appendChild(doc.createTextNode(`\n${indent}`));
}

/** Whether `el` has element children or non-blank text. */
const hasContent = (el: Element) => Array.from(el.childNodes).some((n) => !isBlank(n));

/**
 * Appends `child` after the last element child of `parent`, with the siblings' indentation, and indents `child`'s own
 * subtree ({@link indentTree}). Into an empty `parent` on an indented line, `child` goes one step deeper and `parent`'s
 * closing tag goes back on its own line. In a compact document nothing but `child` is added.
 */
export function append(parent: Element, child: Element): void {
  const last = parent.lastElementChild;
  if (last) {
    const before = last.previousSibling;
    const next = last.nextSibling;
    if (isBlank(before)) parent.insertBefore(before.cloneNode(), next);
    parent.insertBefore(child, next);
    const indent = isBlank(before) ? lastLine(before.nodeValue ?? '') : undefined;
    if (indent !== undefined) indentTree(child, indent, indentStep(parent));
    return;
  }
  const own = ownIndent(parent);
  if (own === undefined || hasContent(parent)) {
    parent.appendChild(child);
    return;
  }
  const step = indentStep(parent);
  for (const n of Array.from(parent.childNodes)) parent.removeChild(n);
  const doc = parent.ownerDocument;
  parent.appendChild(doc.createTextNode(`\n${own}${step}`));
  parent.appendChild(child);
  parent.appendChild(doc.createTextNode(`\n${own}`));
  indentTree(child, own + step, step);
}

/**
 * Inserts the block `el` into `parent` before `ref` with the indentation `ref` has, or at the end ({@link append}), and
 * indents its subtree.
 */
export function insertBlock(parent: Element, el: Element, ref: Element | null): void {
  if (!ref) {
    append(parent, el);
    return;
  }
  const before = ref.previousSibling;
  parent.insertBefore(el, ref);
  if (isBlank(before)) {
    parent.insertBefore(before.cloneNode(), ref);
    const indent = lastLine(before.nodeValue ?? '');
    if (indent !== undefined) indentTree(el, indent, indentStep(parent));
  }
}

/** Removes `node` and the whitespace before it, so the siblings keep their indentation. */
export function detach(node: ChildNode): void {
  const before = node.previousSibling;
  if (isBlank(before)) before.parentNode?.removeChild(before);
  node.parentNode?.removeChild(node);
}

/**
 * Rebuilds the whitespace directly in `<rows>` and in each of its `<row>`s: rows one step in, cells two steps in. A
 * cell's own content is not touched. A no-op when `<rows>` is not on an indented line, or its whitespace has no line
 * break (compact).
 */
export function reindentRows(rows: Element): void {
  const own = ownIndent(rows);
  if (own === undefined) return;
  const blanks = Array.from(rows.childNodes).filter(isBlank);
  if (blanks.length > 0 && !blanks.some((n) => n.nodeValue!.includes('\n'))) return;
  const step = indentStep(rows.parentElement ?? rows);
  const doc = rows.ownerDocument;
  const rebuild = (el: Element, indent: string) => {
    for (const n of Array.from(el.childNodes)) if (isBlank(n)) el.removeChild(n);
    const children = Array.from(el.children);
    for (const child of children) el.insertBefore(doc.createTextNode(`\n${indent}${step}`), child);
    if (children.length > 0) el.appendChild(doc.createTextNode(`\n${indent}`));
  };
  rebuild(rows, own);
  for (const row of Array.from(rows.children)) rebuild(row, own + step);
}
