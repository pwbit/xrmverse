import { describe, expect, it } from 'vitest';
import { parseXml, serializeXml } from './xml';

describe('serializeXml', () => {
  it('writes elements of the XHTML namespace without a namespace and leaves the node unchanged', () => {
    const doc = parseXml('<form><tabs><tab xmlns="http://www.w3.org/1999/xhtml" name="t"><labels/></tab></tabs></form>');
    const header = doc.createElementNS('http://www.w3.org/1999/xhtml', 'header');
    doc.documentElement.appendChild(header);

    const out = serializeXml(doc);

    expect(out).toBe('<form><tabs><tab name="t"><labels/></tab></tabs><header/></form>');
    expect(doc.getElementsByTagName('tab')[0].namespaceURI).toBe('http://www.w3.org/1999/xhtml');
  });

  it('keeps other namespaced attributes', () => {
    const doc = parseXml('<form xmlns:x="urn:x"><tab xmlns="http://www.w3.org/1999/xhtml" x:extra="1"/></form>');
    expect(serializeXml(doc)).toBe('<form xmlns:x="urn:x"><tab x:extra="1"/></form>');
  });
});
