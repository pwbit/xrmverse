import { describe, expect, it } from 'vitest';
import { parseXml } from '../../parsers/xml';
import { append, childIndent, indentStep, indentTree, reindentRows } from './xmlIndent';

const serialize = (n: Node) => new XMLSerializer().serializeToString(n);
const first = (doc: Document, tag: string) => doc.getElementsByTagName(tag)[0];

/** `<a><b><c/></b></a>`, created in `doc` and detached. */
function subtree(doc: Document): Element {
  const a = doc.createElement('a');
  const b = doc.createElement('b');
  b.appendChild(doc.createElement('c'));
  const text = doc.createElement('t');
  text.textContent = 'x';
  b.appendChild(text);
  a.appendChild(b);
  return a;
}

describe('xmlIndent', () => {
  it('append indents a new subtree like its siblings, one step deeper per level', () => {
    const doc = parseXml('<root>\n  <list>\n    <item />\n  </list>\n</root>');
    append(first(doc, 'list'), subtree(doc));
    expect(serialize(doc)).toBe('<root>\n  <list>\n    <item/>\n    <a>\n      <b>\n        <c/>\n        <t>x</t>\n      </b>\n    </a>\n  </list>\n</root>');
  });

  it('append into an empty indented element puts the closing tag back on its line', () => {
    const doc = parseXml('<root>\n  <list />\n  <other>\n    <x />\n  </other>\n</root>');
    const list = first(doc, 'list');
    append(list, doc.createElement('item'));
    append(list, doc.createElement('item2'));
    expect(serialize(doc)).toBe('<root>\n  <list>\n    <item/>\n    <item2/>\n  </list>\n  <other>\n    <x/>\n  </other>\n</root>');
  });

  it('a compact document gains no whitespace', () => {
    const doc = parseXml('<root><list><item/></list><empty/></root>');
    append(first(doc, 'list'), subtree(doc));
    append(first(doc, 'empty'), subtree(doc));
    expect(serialize(doc)).toBe('<root><list><item/><a><b><c/><t>x</t></b></a></list><empty><a><b><c/><t>x</t></b></a></empty></root>');
    expect(childIndent(first(doc, 'list'))).toBeUndefined();
    const rows = parseXml('<rows><row><cell/></row></rows>');
    reindentRows(rows.documentElement);
    expect(serialize(rows)).toBe('<rows><row><cell/></row></rows>');
  });

  it('the step is inferred from the document (4 spaces) or defaults to 2', () => {
    const four = parseXml('<root>\n    <list>\n        <item />\n    </list>\n</root>');
    expect(childIndent(first(four, 'list'))).toBe('        ');
    expect(indentStep(first(four, 'item'))).toBe('    ');
    append(first(four, 'list'), subtree(four));
    expect(serialize(four)).toContain('\n        <a>\n            <b>\n                <c/>');
    const flat = parseXml('<root>\n<list />\n</root>');
    expect(indentStep(first(flat, 'list'))).toBe('  ');
    append(first(flat, 'list'), subtree(flat));
    expect(serialize(flat)).toBe('<root>\n<list>\n  <a>\n    <b>\n      <c/>\n      <t>x</t>\n    </b>\n  </a>\n</list>\n</root>');
  });

  it('indentTree leaves an element whose content is text as it is', () => {
    const doc = parseXml('<root/>');
    const el = doc.createElement('p');
    el.textContent = 'a <b> text';
    indentTree(el, '  ', '  ');
    expect(serialize(el)).toBe('<p>a &lt;b&gt; text</p>');
  });

  it('reindentRows rebuilds the whitespace of rows and row, not the cells', () => {
    const doc = parseXml('<section>\n  <rows>\n    <row>\n      <cell>\n        <labels />\n      </cell>\n    </row>\n  </rows>\n</section>');
    const rows = first(doc, 'rows');
    const cell = doc.createElement('cell');
    cell.appendChild(doc.createElement('labels'));
    const row = doc.createElement('row');
    row.appendChild(cell);
    rows.appendChild(row);
    first(doc, 'row').appendChild(doc.createElement('cell'));
    reindentRows(rows);
    expect(serialize(doc)).toBe('<section>\n  <rows>\n    <row>\n      <cell>\n        <labels/>\n      </cell>\n      <cell/>\n'
      + '    </row>\n    <row>\n      <cell><labels/></cell>\n    </row>\n  </rows>\n</section>');
  });
});
