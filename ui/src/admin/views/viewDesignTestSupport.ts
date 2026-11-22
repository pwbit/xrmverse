import { parseXml } from '../../parsers/xml';

/** Serializes `xml` after dropping whitespace-only text nodes, so two documents compare by content. */
export function normalizeXml(xml: string): string {
  const doc = parseXml(xml);
  const strip = (node: Node) => {
    for (const child of Array.from(node.childNodes)) {
      if (child.nodeType === Node.TEXT_NODE && !child.textContent?.trim()) node.removeChild(child);
      else strip(child);
    }
  };
  strip(doc);
  return new XMLSerializer().serializeToString(doc);
}

/** The inner XML of `<tag>` in a solution `savedquery` file. */
export function innerOf(savedquery: string, tag: 'fetch' | 'grid'): string {
  return new RegExp(`<${tag}[ >][\\s\\S]*?</${tag}>`).exec(savedquery)![0];
}

export const ACCOUNT = { primaryId: 'accountid', primaryName: 'name' };
