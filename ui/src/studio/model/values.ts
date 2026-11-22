/**
 * The document's values as the properties panel edits them. `toJS` of the failsafe document cannot tell the empty value
 * (`~`, `null` written plain) from the text `'~'` or `'null'` (quoted): both are the string. Here the empty value is JS
 * `null` and every other scalar its text, so that writing a value back (`setAt`) keeps what it means.
 */
import { isMap, isScalar, isSeq, Scalar } from 'yaml';
import type { Document } from 'yaml';

const EMPTY = ['', '~', 'null', 'Null', 'NULL'];

/** A node as plain JS: `null` for the empty value, text for any other scalar, objects and arrays for collections. */
export function nodeValue(node: unknown): unknown {
  if (node == null) return null;
  if (isScalar(node)) {
    if (node.value == null) return null;
    const text = String(node.value);
    return node.type === Scalar.PLAIN && EMPTY.includes(text) ? null : text;
  }
  if (isSeq(node)) return node.items.map(nodeValue);
  if (isMap(node)) {
    const out: Record<string, unknown> = {};
    for (const pair of node.items) {
      const key = isScalar(pair.key) ? String(pair.key.value) : String(pair.key);
      out[key] = nodeValue(pair.value);
    }
    return out;
  }
  return undefined;
}

/** The value at `path` (see `nodeValue`); undefined when the key is not there. */
export function valueAt(doc: Document, path: (string | number)[]): unknown {
  const node = path.length === 0 ? doc.contents : doc.getIn(path, true);
  if (node === undefined) return undefined;
  return nodeValue(node);
}
