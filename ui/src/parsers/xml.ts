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
