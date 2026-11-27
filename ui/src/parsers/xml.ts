/** Parses XML text; a malformed document throws instead of yielding a `parsererror` tree. */
export function parseXml(xml: string): Document {
  const doc = new DOMParser().parseFromString(xml, 'application/xml');
  const error = doc.getElementsByTagName('parsererror')[0];
  if (error) throw new Error(`Malformed XML: ${error.textContent?.trim().split('\n')[0] ?? ''}`);
  return doc;
}

export function childElements(parent: Element, name: string): Element[] {
  return Array.from(parent.children).filter((c) => c.tagName === name);
}

const XHTML = 'http://www.w3.org/1999/xhtml';
const XMLNS = 'http://www.w3.org/2000/xmlns/';

/**
 * Serializes `node` as plain XML: an element in the XHTML namespace (which a browser can give an element created in a
 * document it treats as XHTML) is written without a namespace, as FormXML, LayoutXML and FetchXML have none. The node
 * itself is not changed.
 */
export function serializeXml(node: Node): string {
  const copy = node.cloneNode(true);
  const root = copy.nodeType === Node.DOCUMENT_NODE ? (copy as Document).documentElement : (copy as Element);
  if (root) withoutXhtml(root);
  return new XMLSerializer().serializeToString(copy);
}

function withoutXhtml(el: Element): void {
  for (const child of Array.from(el.children)) withoutXhtml(child);
  if (el.namespaceURI !== XHTML) return;
  const plain = el.ownerDocument.createElementNS(null, el.localName);
  for (const a of Array.from(el.attributes)) {
    if (a.namespaceURI === XMLNS) continue;
    if (a.namespaceURI) plain.setAttributeNS(a.namespaceURI, a.name, a.value);
    else plain.setAttribute(a.name, a.value);
  }
  while (el.firstChild) plain.appendChild(el.firstChild);
  el.parentNode?.replaceChild(plain, el);
}
